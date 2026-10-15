using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Agents;
using CloudAgent.Core.Loop;
using CloudAgent.Core.Indexing;
using CloudAgent.Core.Memory;
using CloudAgent.Core.Providers;
using CloudAgent.Core.Surfaces;
using CloudAgent.Surfaces;
using CloudAgent.Core.Tools;
using CloudAgent.Core.Tools.Agents;
using CloudAgent.Core.Tools.Index;
using CloudAgent.Core.Tools.Memory;
using CloudAgent.Core.Tools.Todos;
using CloudAgent.Core.Tools.Web;
using CloudAgent.Core.Todos;
using CloudAgent.Core.Workspaces;
using CloudAgent.Core.Attention;
using CloudAgent.Host;
using CloudAgent.Host.Configuration;

// ---- Wiring -----------------------------------------------------------------
// All environment resolution lives in HostConfig; the composition root below only constructs.
// Provider/LLM selection (LlmConfig + ProviderFactory) keeps the precedence — highest first:
// Claude CLI → Foundry → Anthropic → echo. Foundry is the production choice (one endpoint
// serves the whole catalog; background jobs run on a cheap model via AGENT_SMALL_MODEL).
var config = HostConfig.FromEnvironment();
var providers = new ProviderFactory(config.Llm);
var rawProvider = providers.CreateLlm();
var isEcho = rawProvider is EchoLlmProvider;
var meter = new MeteredLlmProvider(rawProvider);
meter.Completed += (m, r, elapsed) =>
{
    var total = meter.Usage.First(u => u.Model == m);
    Console.WriteLine($"  🔢 {m}: {r.InputTokens ?? 0} in, {r.OutputTokens ?? 0} out, {elapsed.TotalMilliseconds:0} ms " +
                      $"(total {total.Calls} calls, {total.InputTokens} in, {total.OutputTokens} out)");
};
ILlmProvider provider = meter;
var model = config.Llm.Model;

// The agent's world: a rooted area it may act within (env AGENT_WORKSPACE, else ./workspace).
IWorkspace workspace = new Workspace(config.WorkspaceRoot);

// Web search backend: Tavily if a key is present, otherwise unconfigured.
IWebSearchProvider search = string.IsNullOrWhiteSpace(config.TavilyApiKey)
    ? new NullWebSearchProvider()
    : new TavilyWebSearchProvider(config.TavilyApiKey);

// Embeddings backend: Foundry if its endpoint is set, else a deterministic offline hash.
IEmbeddingProvider embeddings = providers.CreateEmbeddings();

// Semantic index over the workspace, persisted under .agent/index.json.
var indexPath = Path.Combine(workspace.Root, ".agent", "index.json");
var index = VectorIndex.Load(indexPath);
var indexer = new WorkspaceIndexer(workspace, embeddings, index);

// Background jobs (summaries, consolidation, utility judging, state snapshots) run on a
// cheap catalog model when one is configured; the main loop keeps the big one.
var smallModel = config.Llm.SmallModel;
var summarizer = new Summarizer(provider, smallModel);

// Workspace tools shared by every agent — they all operate on the same workspace.
var workspaceTools = Toolset.CreateDefault(workspace, search)
    .Register(new IndexWorkspaceTool(indexer, index, indexPath))
    .Register(new SearchIndexTool(embeddings, index))
    .Register(new SummarizeFileTool(workspace, summarizer));

// Web reading at three levels of detail; fetched pages are cached under .agent/web.
var webReader = new WebReader(
    new WebFetcher(),
    new WebPageStore(Path.Combine(workspace.Root, ".agent", "web")),
    provider,
    smallModel);
workspaceTools
    .Register(new FetchUrlTool(webReader))
    .Register(new AskPageTool(webReader))
    .Register(new ReadPageTool(webReader));

// Taskboard: a persistent todo list in the workspace (one board, shared).
var todos = new TodoStore(Path.Combine(workspace.Root, ".agent", "todos.json"));
workspaceTools
    .Register(new TodoListTool(todos))
    .Register(new TodoAddTool(todos))
    .Register(new TodoCompleteTool(todos))
    .Register(new TodoRemoveTool(todos))
    .Register(new TodoClearTool(todos));

// Memory paths: Personal is user-global, Project is shared; each agent gets its OWN session
// store, so children do not share the parent's memory.
var personalMemoryPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".cloudagent", "memory", "personal.db");
var projectMemoryPath = Path.Combine(workspace.Root, ".agent", "memory", "project.db");

// Utility feedback loop: a shared append-only ledger of which injected memories actually
// helped, judged post-turn. Judging (and state snapshotting) need a real model, so both are
// off under the echo provider to keep the ledger clean.
var utilityLedger = new FileUtilityLedger(Path.Combine(workspace.Root, ".agent", "memory", "utility.jsonl"));
var utilityJudge = isEcho ? null : new UtilityJudge(provider, smallModel);
var snapshotter = isEcho ? null : new StateSnapshotter(provider, smallModel);

// Read-side pipeline: rewrite the query before searching, rerank the wide hybrid pass down
// to the few genuinely useful memories. Both on the small model; off under echo.
var queryRewriter = isEcho ? null : new QueryRewriter(provider, smallModel);
var reranker = isEcho ? null : new MemoryReranker(provider, smallModel);

// Sleep-time consolidation: tamper-evident ledger of every decision, a staging area for
// distilled knowledge (applied only through the approval tools), and the written policy
// that judges distillation — editable prose, versioned in git, injected as the system
// prompt of the sleep pass.
var consolidationLedger = new ConsolidationLedger(
    Path.Combine(workspace.Root, ".agent", "memory", "consolidation.jsonl"));
var pendingConsolidations = new PendingConsolidations(
    Path.Combine(workspace.Root, ".agent", "memory", "pending"));

var policyPath = config.ConsolidationPolicyPath;
var consolidationPolicy = File.Exists(policyPath)
    ? File.ReadAllText(policyPath)
    : "You are the memory-consolidation process of an autonomous agent. Only propose a fact " +
      "when the same lesson appears in at least 3 independent episodes; cite the episode " +
      "numbers as evidence. Never distill secrets or one-off remarks. Prefer few, " +
      "well-grounded facts over many speculative ones.";

var sleepJobs = new System.Collections.Concurrent.ConcurrentBag<SleepJob>();

var options = new AgentOptions
{
    SystemPrompt =
        "You are an autonomous agent running on your own machine with a persistent workspace. " +
        "Operate with the initiative of a capable colleague. You can delegate scoped work to child " +
        "agents (spawn_agent / delegate_task) and answer their questions. Be concise.",
    Model = model,
    IterationDelay = TimeSpan.Zero,
    WorkingMemoryBudgetTokens = config.MemoryBudgetTokens,
};

var childOptions = new AgentOptions
{
    SystemPrompt =
        "You are a subagent working on a scoped task from your parent. You have your own memory and " +
        "the shared workspace. Use ask_parent for a decision you can't make; call finish with your " +
        "result when done. Be concise.",
    Model = model,
    IterationDelay = TimeSpan.Zero,
    WorkingMemoryBudgetTokens = config.MemoryBudgetTokens,
};

// Attention (eje 5) knobs — recall shape and budget — live with the module, not AgentOptions.
var attentionOptions = new AttentionOptions();

// Wrap the assembler in the metacognitive gate (eje 8) when enabled: it suppresses injected
// memories whose measured track record says they keep not helping, logging each suppression.
IAttentionManager BuildAttention(string persona, MemoryRetriever retriever, string label)
{
    IAttentionManager attention = new ContextAssembler(persona, retriever, attentionOptions);
    if (attentionOptions.MetacognitionEnabled)
        attention = new MetacognitiveAttentionManager(
            attention, utilityLedger, attentionOptions,
            onSuppressed: hit => Console.WriteLine(
                $"  🚫 [{label}] suppressed low-confidence memory: " +
                (hit.Entry.Content.Length <= 80 ? hit.Entry.Content : hit.Entry.Content[..80] + "…")));
    return attention;
}

// Per-agent memory + compaction stack: own session memory, shared personal/project knowledge,
// shared utility ledger (memories are shared, so their track record is too). The hybrid
// SQLite store (FTS5 + vector, RRF-fused) replaces the JSON files; memory embeddings use the
// same backend as the index (Foundry when configured — pennies — else the offline hash).
(MemoryManager Memory, ContextCompactor Compactor, UtilityTracker Utility, MemoryRetriever Retriever)
    BuildMemoryStack(string agentId)
{
    var store = new SqliteMemoryStore(new Dictionary<MemoryScope, string>
    {
        [MemoryScope.Personal] = personalMemoryPath,
        [MemoryScope.Project] = projectMemoryPath,
        [MemoryScope.Session] = Path.Combine(workspace.Root, ".agent", "memory", "agents", $"{agentId}.db"),
    });

    // Ambient metadata stamped on every write: who was writing, where, in which conversation.
    var separator = agentId.IndexOf(':');
    var ambient = new Dictionary<string, string> { ["agent"] = agentId };
    if (separator > 0)
    {
        ambient["surface"] = agentId[..separator];
        ambient["conversation"] = agentId[(separator + 1)..];
    }

    var mem = new MemoryManager(store, embeddings, ambientMetadata: ambient);
    var comp = new ContextCompactor(summarizer, options, mem, new Consolidator(provider, mem, smallModel), snapshotter);
    var utility = new UtilityTracker(utilityLedger, utilityJudge, agentId);
    var retriever = new MemoryRetriever(mem, queryRewriter, reranker);

    if (!isEcho)
        sleepJobs.Add(new SleepJob(
            provider, mem, utilityLedger, consolidationLedger, pendingConsolidations,
            agentId, consolidationPolicy, smallModel));

    return (mem, comp, utility, retriever);
}

void AttachLogging(AgentLoop loop, string label)
{
    long? turnStarted = null;
    loop.InputReceived += _ => turnStarted = System.Diagnostics.Stopwatch.GetTimestamp();
    loop.ResponseReceived += (_, r) =>
    {
        if (turnStarted is { } started && !string.IsNullOrWhiteSpace(r.Text))
        {
            Console.WriteLine($"  ⏱ [{label}] reply after {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
            turnStarted = null;
        }
        if (r.CacheReadTokens is > 0 || r.CacheWriteTokens is > 0)
            Console.WriteLine(
                $"  💰 [{label}] prompt tokens — {r.CacheReadTokens ?? 0} cached, " +
                $"{r.CacheWriteTokens ?? 0} written, {r.InputTokens ?? 0} fresh");
    };
    loop.ToolInvoked += (call, result) =>
        Console.WriteLine($"  ⚙ [{label}] {call.Name} -> {(result.Success ? "ok" : "error")}");
    loop.TurnFailed += (i, ex) => Console.Error.WriteLine($"  ✖ [{label}] turn {i} failed: {ex.GetType().Name}: {ex.Message}");
    loop.Compacted += n => Console.WriteLine($"  🧠 [{label}] compacted {n} turn(s)");
    loop.MemoriesRecalled += n => Console.WriteLine($"  🧠 [{label}] recalled {n} memor(ies)");
    loop.Consolidated += n => Console.WriteLine($"  🧠 [{label}] consolidated {n} fact(s)");
    loop.UtilityJudged += (useful, total) =>
        Console.WriteLine($"  📊 [{label}] memories useful this turn: {useful}/{total}");
}

// Ctrl+C or typing "exit" stops everything cleanly.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Multi-agent: a bus + orchestrator so any agent can spawn children that run concurrently.
var bus = new AgentBus(cts.Token);
bus.StatusChanged += h => Console.WriteLine($"  🤖 {h.Id} ({h.Role}) -> {h.Status}");

SubagentLoopBuilder buildChild = (child, input, childTools) =>
{
    var (mem, comp, utility, retriever) = BuildMemoryStack(child.Id);
    childTools.Register(new RememberTool(mem));
    var attention = BuildAttention(childOptions.SystemPrompt, retriever, $"child {child.Role}");
    var loop = new AgentLoop(provider, childOptions, input, new AgentContext(), childTools, comp, attention, utility);
    AttachLogging(loop, $"child {child.Role}");
    return loop;
};

var orchestrator = new AgentOrchestrator(bus, workspaceTools, buildChild);

// Each conversation is a root agent that can spawn children. It perceives the human (its
// surface queue) and its children (its mailbox) through a composite input.
AgentConversation CreateConversation(string surface, string conversationId)
{
    var handle = bus.Register(role: surface, id: $"{surface}:{conversationId}");
    var queue = new QueueInputSource();
    var input = new CompositeInputSource(queue, bus.InputFor(handle));
    var (mem, comp, utility, retriever) = BuildMemoryStack(handle.Id);

    var agentTools = new ToolRegistry();
    foreach (var tool in workspaceTools.All)
        agentTools.Register(tool);
    agentTools
        .Register(new RememberTool(mem))
        .Register(new ListConsolidationsTool(pendingConsolidations))
        .Register(new ApplyConsolidationTool(pendingConsolidations, mem, consolidationLedger))
        .Register(new SpawnAgentTool(orchestrator, handle))
        .Register(new DelegateTaskTool(orchestrator, handle))
        .Register(new ListAgentsTool(bus, handle))
        .Register(new ReplyToAgentTool(bus, handle))
        .Register(new StopAgentTool(bus));

    var attention = BuildAttention(options.SystemPrompt, retriever, handle.Id);
    var loop = new AgentLoop(provider, options, input, new AgentContext(), agentTools, comp, attention, utility);
    AttachLogging(loop, handle.Id);
    return new AgentConversation(queue, loop);
}

// Surfaces: the console is always on; others join when configured.
var surfaces = new List<ISurface> { new CliSurface(cts) };

if (!string.IsNullOrWhiteSpace(config.TelegramBotToken))
    surfaces.Add(new TelegramSurface(config.TelegramBotToken));

if (!string.IsNullOrWhiteSpace(config.DiscordBotToken))
    surfaces.Add(new DiscordSurface(config.DiscordBotToken));

if (config.WebchatEnabled)
{
    var webchat = new WebchatSurface(config.WebchatPort);
    surfaces.Add(webchat);
    Console.WriteLine($"Web chat:  {webchat.Url}");
}

var turnTaking = config.TurnTakingEnabled
    ? new TurnTakingOptions()
    : new TurnTakingOptions { ImmediateSurfaces = surfaces.Select(s => s.Name).ToHashSet() };
using var router = new ConversationRouter(surfaces, CreateConversation, turnTaking: turnTaking);
router.ConversationOpened += (s, c) => Console.WriteLine($"  ✉ conversation opened: {s}:{c}");

// Sleep timer: every interval, run each registered agent's consolidation pass. Mechanical
// strata moves auto-apply; distillation only stages proposals for review.
var sleepMinutes = config.SleepIntervalMinutes;
if (!isEcho && sleepMinutes > 0)
{
    _ = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(sleepMinutes), cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            foreach (var job in sleepJobs)
            {
                if (cts.Token.IsCancellationRequested)
                    break;

                try
                {
                    var result = await job.RunAsync(cts.Token);
                    if (result.Demoted > 0 || result.Promoted > 0 || result.StagedProposalId is not null)
                        Console.WriteLine(
                            $"  😴 sleep: {result.Demoted} demoted, {result.Promoted} strata move(s), " +
                            $"staged: {result.StagedProposalId ?? "none"}");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // sleep is best-effort; it gets another chance next interval
                }
            }
        }
    });
}

// ---- Run --------------------------------------------------------------------
Console.WriteLine($"CloudAgent harness — provider: {provider.Name}");
Console.WriteLine($"Workspace: {workspace.Root}");
Console.WriteLine($"Search:    {search.Name}");
Console.WriteLine($"Embeddings:{embeddings.Name} (index: {index.Count} chunk(s))");
Console.WriteLine($"Memory:    sqlite hybrid (FTS5 + vector, RRF); budget {options.WorkingMemoryBudgetTokens} tok");
Console.WriteLine($"Recall:    rewrite {(queryRewriter is null ? "off" : "on")}, rerank {(reranker is null ? "off" : "on")}, budget {attentionOptions.RecallBudgetTokens} tok");
Console.WriteLine($"Metacog:   {(attentionOptions.MetacognitionEnabled ? $"on (suppress after {attentionOptions.MetacognitionMinInjections} injections at ≤{attentionOptions.MetacognitionMaxUsefulRate:0.##} useful rate)" : "off")}");
Console.WriteLine($"Utility:   ledger with {utilityLedger.Count} event(s); judge {(utilityJudge is null ? "off (echo provider)" : "on")}");
Console.WriteLine($"Models:    main '{model}', background '{smallModel}'");
Console.WriteLine($"Sleep:     {(isEcho || sleepMinutes <= 0 ? "off" : $"every {sleepMinutes} min")}; " +
    $"pending proposals: {pendingConsolidations.List().Count}; ledger chain {(consolidationLedger.Verify() ? "OK" : "BROKEN")}");
Console.WriteLine($"Taskboard: {todos.Count} task(s)");
Console.WriteLine($"Tools:     {string.Join(", ", workspaceTools.All.Select(t => t.Name))}");
Console.WriteLine($"Agent:     remember + spawn_agent, delegate_task, list_agents, reply_to_agent, stop_agent");
Console.WriteLine($"Surfaces:  {string.Join(", ", surfaces.Select(s => s.Name))}");
Console.WriteLine($"Turns:     {(config.TurnTakingEnabled ? $"bursts (wait {turnTaking.DefaultWait.TotalSeconds:0.#}s until measured; cli immediate)" : "one message per turn")}");

await router.RunAsync(cts.Token);

Console.WriteLine("\nAgent stopped.");
