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
using CloudAgent.Core.Tools.Compute;
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

// The host's log goes to the console and to a daily file in the workspace.
var logDirectory = Path.Combine(config.WorkspaceRoot, ".agent", "logs");
Directory.CreateDirectory(logDirectory);
var logFile = new StreamWriter(
    Path.Combine(logDirectory, $"host-{DateTime.Now:yyyy-MM-dd}.log"), append: true, System.Text.Encoding.UTF8);
logFile.WriteLine($"---- host started {DateTimeOffset.Now:O}");
Console.SetOut(new TeeTextWriter(Console.Out, logFile));
Console.SetError(new TeeTextWriter(Console.Error, logFile));

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
// A token budget, when set, wraps every model call: the agents' turns and every background job.
var budget = config.TokenBudget is { } limit
    ? new TokenBudget(limit, Path.Combine(config.WorkspaceRoot, ".agent", "usage.json"))
    : null;
ILlmProvider provider = budget is null ? meter : new BudgetedLlmProvider(meter, budget);
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
    .Register(new SummarizeFileTool(workspace, summarizer))
    .Register(new CalculateTool());

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
var personalMemoryPath = config.PersonalMemoryPath;
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

var sleepJobs = new System.Collections.Concurrent.ConcurrentDictionary<string, SleepJob>();

// Conversations that never compact would leave nothing in memory; a learner per conversation
// archives and stages what was said once the conversation goes quiet.
var learners = new System.Collections.Concurrent.ConcurrentDictionary<string, SessionLearner>();

var options = new AgentOptions
{
    SystemPrompt =
        "You are an autonomous agent running on your own machine with a persistent workspace. " +
        "Operate with the initiative of a capable colleague. You can delegate scoped work to child " +
        "agents (spawn_agent / delegate_task) and answer their questions. Never work out numbers in your " +
        "head: compute every figure you report with the calculate tool. Be concise.",
    Model = model,
    IterationDelay = TimeSpan.Zero,
    Budget = budget,
    WorkingMemoryBudgetTokens = config.MemoryBudgetTokens,
};

var childOptions = new AgentOptions
{
    SystemPrompt =
        "You are a subagent working on a scoped task from your parent. You have your own memory and " +
        "the shared workspace. Use ask_parent for a decision you can't make; call finish with your " +
        "result when done. Compute every figure you report with the calculate tool. Be concise.",
    Model = model,
    IterationDelay = TimeSpan.Zero,
    Budget = budget,
    WorkingMemoryBudgetTokens = config.MemoryBudgetTokens,
};

// Attention (eje 5) knobs — recall shape and budget — live with the module, not AgentOptions.
var attentionOptions = new AttentionOptions();

// Profiles: the same agent in different workspaces. The general profile works in the agent's own
// workspace; each configured repository gets a coding profile with its own tools, index, and
// project memory, kept under the agent's workspace so the repository itself stays clean.
var generalProfile = new AgentProfile("general", options, childOptions, workspace, workspaceTools, projectMemoryPath);
var codeProfiles = new Dictionary<string, AgentProfile>(StringComparer.OrdinalIgnoreCase);
foreach (var repo in config.CodeRepos)
{
    var root = Path.GetFullPath(repo.Path).TrimEnd(Path.DirectorySeparatorChar);
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"  ✖ code repo '{repo.Name}' skipped: {root} does not exist");
        continue;
    }
    var running = Path.GetFullPath(AppContext.BaseDirectory);
    if (running.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(
            $"  ✖ code repo '{repo.Name}' skipped: the host runs from inside it. Point it at a git worktree instead.");
        continue;
    }
    codeProfiles[repo.Name] = BuildCodeProfile(repo.Name, root);
}

AgentProfile BuildCodeProfile(string name, string root)
{
    var repoWorkspace = new Workspace(root, ExecutionPolicy.Allow(config.CodeCommands));
    var state = Path.Combine(workspace.Root, ".agent", "repos", name);
    var repoIndexPath = Path.Combine(state, "index.json");
    var repoIndex = VectorIndex.Load(repoIndexPath);

    var tools = Toolset.CreateCode(repoWorkspace, search)
        .Register(new IndexWorkspaceTool(new WorkspaceIndexer(repoWorkspace, embeddings, repoIndex), repoIndex, repoIndexPath))
        .Register(new SearchIndexTool(embeddings, repoIndex))
        .Register(new SummarizeFileTool(repoWorkspace, summarizer));
    foreach (var tool in workspaceTools.All.Where(t =>
                 t.Name is "calculate" or "fetch_url" or "ask_page" or "read_page" || t.Name.StartsWith("todo_")))
        tools.Register(tool);

    var rules =
        $" You are working in the {name} repository; your file tools act inside it. Read a file before you change it, " +
        "and change it with edit_file. Find code with search_files. After changing code, build it and run its tests " +
        "with run_command, and fix what fails before you say you are done. Check git status and git diff before you " +
        "finish. Do not commit, push, or switch branches unless you are asked to.";
    AgentOptions Coding(AgentOptions basis) => new()
    {
        SystemPrompt = basis.SystemPrompt + rules,
        Model = basis.Model,
        IterationDelay = basis.IterationDelay,
        Budget = basis.Budget,
        WorkingMemoryBudgetTokens = basis.WorkingMemoryBudgetTokens,
        MaxToolSteps = 40,
    };
    return new AgentProfile(
        name, Coding(options), Coding(childOptions), repoWorkspace, tools, Path.Combine(state, "project.db"), Routed: false);
}

// The profile each running agent was built with, so its subagents inherit it.
var agentProfiles = new System.Collections.Concurrent.ConcurrentDictionary<string, AgentProfile>();
AgentProfile ProfileOf(string? agentId) =>
    agentId is not null && agentProfiles.TryGetValue(agentId, out var profile) ? profile : generalProfile;

// Wrap the assembler in the metacognitive gate (eje 8) when enabled: it suppresses injected
// memories whose measured track record says they keep not helping, logging each suppression.
IAttentionManager BuildAttention(string persona, MemoryRetriever retriever, string label, string agentId)
{
    IAttentionManager attention = new ContextAssembler(persona, retriever, attentionOptions);
    if (attentionOptions.MetacognitionEnabled)
        attention = new MetacognitiveAttentionManager(
            attention, utilityLedger, attentionOptions, agentId,
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
    BuildMemoryStack(string agentId, string projectPath, ConversationKind? kind = null)
{
    var store = new SqliteMemoryStore(new Dictionary<MemoryScope, string>
    {
        [MemoryScope.Personal] = personalMemoryPath,
        [MemoryScope.Project] = projectPath,
        [MemoryScope.Session] = SessionMemoryPath(agentId),
    });

    // Ambient metadata stamped on every write: who was writing, where, in which conversation.
    var separator = agentId.IndexOf(':');
    var ambient = new Dictionary<string, string> { ["agent"] = agentId };
    if (separator > 0)
    {
        ambient["surface"] = agentId[..separator];
        ambient["conversation"] = agentId[(separator + 1)..];
    }
    if (kind is not null)
        ambient["kind"] = kind == ConversationKind.Direct ? "direct" : "group";

    var mem = new MemoryManager(store, embeddings, ambientMetadata: ambient);
    var comp = new ContextCompactor(summarizer, options, mem, new Consolidator(provider, mem, smallModel), snapshotter);
    var utility = new UtilityTracker(utilityLedger, utilityJudge, agentId);
    var retriever = new MemoryRetriever(mem, queryRewriter, reranker);

    if (!isEcho)
        sleepJobs[agentId] = new SleepJob(
            provider, mem, utilityLedger, consolidationLedger, pendingConsolidations,
            agentId, consolidationPolicy, smallModel);

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
    loop.Routed += d =>
    {
        var applied = $"memory {d.Memory}, tools {(d.Categories.Count == 0 ? "core only" : string.Join(", ", d.Categories.Order()))}";
        var note = d.FellBack ? " (router failed, full pipeline)"
            : d.Shadow ? $" (shadow; router chose memory {d.ProposedMemory}, tools {string.Join(", ", d.ProposedCategories.Order())})"
            : "";
        var guided = d.Guidance.Count == 0 ? "" : $", guidance {d.Guidance.Count}";
        Console.WriteLine($"  🧭 [{label}] {applied}{guided}{note}, decided in {d.Elapsed.TotalMilliseconds:0} ms");
    };
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

// Every agent's loop is recorded event by event, one JSONL file per agent.
var transcriptDirectory = Path.Combine(workspace.Root, ".agent", "transcripts");

// Each conversation's working context is saved after every turn, so it continues after a restart.
var contexts = new ContextStore(Path.Combine(workspace.Root, ".agent", "conversations"));

// Which repository each conversation works in; the rest work in the agent's own workspace.
var conversationWorkspaces = new ConversationWorkspaces(
    Path.Combine(workspace.Root, ".agent", "conversations", "workspaces.json"));

// Context routing: a decision model chooses per turn how much memory to recall and which tool
// categories to offer. Off by default; every uncertain or failed decision runs the full pipeline.
var routingOptions = new RoutingOptions
{
    ToolCategories = new Dictionary<string, string>
    {
        ["web_search"] = "web", ["fetch_url"] = "web", ["ask_page"] = "web", ["read_page"] = "web",
        ["read_file"] = "files", ["write_file"] = "files", ["list_directory"] = "files",
        ["create_directory"] = "files", ["delete_path"] = "files", ["index_workspace"] = "files",
        ["search_index"] = "files", ["summarize_file"] = "files",
        ["todo_list"] = "tasks", ["todo_add"] = "tasks", ["todo_complete"] = "tasks",
        ["todo_remove"] = "tasks", ["todo_clear"] = "tasks",
        ["spawn_agent"] = "agents", ["delegate_task"] = "agents", ["list_agents"] = "agents",
        ["reply_to_agent"] = "agents", ["stop_agent"] = "agents",
        ["list_consolidations"] = "consolidation", ["apply_consolidation"] = "consolidation",
    },
    CategoryDescriptions = new Dictionary<string, string>
    {
        ["web"] = "searching the web or reading a web page",
        ["files"] = "reading, writing, listing, or searching files in the agent's workspace",
        ["tasks"] = "the agent's task list",
        ["agents"] = "delegating work to other agents or managing them",
        ["consolidation"] = "reviewing or applying proposed long-term memory changes",
    },
    LedgerPath = Path.Combine(workspace.Root, ".agent", "routing.jsonl"),
    Guidance =
    [
        new RoutingGuidance(
            "exact_numbers",
            "Does answering the latest message involve calculating, converting, or stating exact numbers?",
            "This turn involves numbers. Compute every figure with the calculate tool and report its results; do not do arithmetic yourself."),
    ],
};
IDecisionModel? decisionModel = config.ContextRouting switch
{
    "decision" when config.DecisionsEndpoint is not null && !string.IsNullOrWhiteSpace(config.DecisionsModel) =>
        new FoundryDecisionModel(config.DecisionsEndpoint, config.DecisionsModel, config.Llm.FoundryApiKey),
    "generative" when !isEcho => new GenerativeDecisionModel(provider, smallModel),
    _ => null,
};

ContextRouter? BuildRouter(MemoryManager memory) =>
    decisionModel is null ? null : new ContextRouter(decisionModel, routingOptions, () => memory.Store.Count);

void RegisterColdRecovery(ToolRegistry tools, MemoryManager mem, string agentId) => tools
    .Register(new RestoreMemoryTool(mem, utilityLedger, consolidationLedger, agentId, attentionOptions.RecallScopes))
    .Register(new SearchMemoryTool(mem, attentionOptions.RecallScopes, attentionOptions.ColdStubPreviewChars));

// Multi-agent: a bus + orchestrator so any agent can spawn children that run concurrently.
var bus = new AgentBus(cts.Token);
bus.StatusChanged += h => Console.WriteLine($"  🤖 {h.Id} ({h.Role}) -> {h.Status}");

SubagentLoopBuilder buildChild = (child, input, childTools) =>
{
    var profile = ProfileOf(child.ParentId);
    agentProfiles[child.Id] = profile;
    var (mem, comp, utility, retriever) = BuildMemoryStack(child.Id, profile.ProjectMemoryPath);
    childTools.Register(new RememberTool(mem));
    RegisterColdRecovery(childTools, mem, child.Id);
    var attention = BuildAttention(profile.ChildOptions.SystemPrompt, retriever, $"child {child.Role}", child.Id);
    var loop = new AgentLoop(
        provider, profile.ChildOptions, input, new AgentContext(), childTools, comp, attention, utility,
        profile.Routed ? BuildRouter(mem) : null);
    AttachLogging(loop, $"child {child.Role}");
    new TranscriptRecorder(transcriptDirectory, child.Id).Attach(loop);
    return loop;
};

var orchestrator = new AgentOrchestrator(bus, parentId => ProfileOf(parentId).Tools, buildChild);

// Each conversation is a root agent that can spawn children. It perceives the human (its
// surface queue) and its children (its mailbox) through a composite input.
AgentConversation CreateConversation(string surface, string conversationId, ConversationKind kind)
{
    var handle = bus.Register(role: surface, id: $"{surface}:{conversationId}");
    var queue = new QueueInputSource();
    var input = new CompositeInputSource(queue, bus.InputFor(handle));
    var profile = conversationWorkspaces.Of(handle.Id) is { } bound && codeProfiles.TryGetValue(bound, out var code)
        ? code
        : generalProfile;
    agentProfiles[handle.Id] = profile;
    var (mem, comp, utility, retriever) = BuildMemoryStack(handle.Id, profile.ProjectMemoryPath, kind);

    var agentTools = new ToolRegistry();
    foreach (var tool in profile.Tools.All)
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
    RegisterColdRecovery(agentTools, mem, handle.Id);

    var attention = BuildAttention(profile.Options.SystemPrompt, retriever, handle.Id, handle.Id);
    var context = contexts.Load(handle.Id);
    if (context is not null)
        Console.WriteLine($"  ↻ resumed {handle.Id} with {context.Messages.Count} message(s)");
    var loop = new AgentLoop(
        provider, profile.Options, input, context ?? new AgentContext(), agentTools, comp, attention, utility,
        profile.Routed ? BuildRouter(mem) : null);
    if (!isEcho)
    {
        var learner = new SessionLearner(
            mem, new Consolidator(provider, mem, smallModel), pendingConsolidations, consolidationLedger, handle.Id,
            () => loop.Context.Messages);
        loop.InputReceived += _ => learner.Touch(busy: true);
        loop.TurnEnded += _ => learner.Touch(busy: false);
        learners[handle.Id] = learner;
    }
    loop.TurnEnded += _ =>
    {
        try
        {
            contexts.Save(handle.Id, loop.Context);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"  ✖ [{handle.Id}] could not save the conversation: {ex.Message}");
        }
    };
    AttachLogging(loop, handle.Id);
    new TranscriptRecorder(transcriptDirectory, handle.Id).Attach(loop);
    return new AgentConversation(queue, loop, onDispose: () =>
    {
        sleepJobs.TryRemove(handle.Id, out _);
        learners.TryRemove(handle.Id, out _);
        agentProfiles.TryRemove(handle.Id, out _);
        bus.Remove(handle.Id);
        (mem.Store as IDisposable)?.Dispose();
    });
}

string SessionMemoryPath(string agentId) =>
    Path.Combine(workspace.Root, ".agent", "memory", "agents", $"{TranscriptRecorder.ToFileName(agentId)}.db");

// Deleting a conversation ends its session and removes its transcript, saved context, and session
// memory. What it taught the shared personal and project memory stays, and so do the ledgers.
async Task<bool> DeleteConversationAsync(ConversationRouter router, string surface, string conversationId)
{
    var agentId = $"{surface}:{conversationId}";
    await router.CloseAsync(surface, conversationId);

    var transcript = Path.Combine(transcriptDirectory, $"{TranscriptRecorder.ToFileName(agentId)}.jsonl");
    var memory = SessionMemoryPath(agentId);
    var files = new[] { transcript, contexts.PathFor(agentId), memory, memory + "-wal", memory + "-shm" };

    var existed = conversationWorkspaces.Of(agentId) is not null;
    conversationWorkspaces.Forget(agentId);
    foreach (var file in files.Where(File.Exists))
    {
        File.Delete(file);
        existed = true;
    }
    if (existed)
        Console.WriteLine($"  🗑 conversation deleted: {agentId}");
    return existed;
}

// Surfaces: the console is always on; others join when configured.
var surfaces = new List<ISurface> { new CliSurface(cts) };

if (!string.IsNullOrWhiteSpace(config.TelegramBotToken))
    surfaces.Add(new TelegramSurface(config.TelegramBotToken));

if (!string.IsNullOrWhiteSpace(config.DiscordBotToken))
    surfaces.Add(new DiscordSurface(config.DiscordBotToken));

ConversationRouter? conversationRouter = null;
if (config.WebchatEnabled)
{
    var webchat = new WebchatSurface(
        config.WebchatPort,
        conversationId => TranscriptRecorder.ReadConversation(transcriptDirectory, $"webchat:{conversationId}"),
        () => TranscriptRecorder.ListConversations(transcriptDirectory, "webchat"),
        conversationId => conversationRouter is null
            ? Task.FromResult(false)
            : DeleteConversationAsync(conversationRouter, "webchat", conversationId),
        new WebchatWorkspaces(
            codeProfiles.Keys.Order().ToList(),
            conversationId => conversationWorkspaces.Of($"webchat:{conversationId}"),
            (conversationId, name) => conversationWorkspaces.Bind($"webchat:{conversationId}", name)));
    surfaces.Add(webchat);
    Console.WriteLine($"Web chat:  {webchat.Url}");
}

var turnTaking = config.TurnTakingEnabled
    ? new TurnTakingOptions()
    : new TurnTakingOptions { ImmediateSurfaces = surfaces.Select(s => s.Name).ToHashSet() };
using var router = new ConversationRouter(surfaces, CreateConversation, turnTaking: turnTaking);
conversationRouter = router;
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

            if (budget is { Exhausted: true })
                continue;

            foreach (var (id, learner) in learners)
            {
                if (cts.Token.IsCancellationRequested || !learner.IsDue(TimeSpan.FromMinutes(config.LearnIdleMinutes)))
                    continue;
                try
                {
                    var staged = await learner.LearnAsync(cts.Token);
                    Console.WriteLine($"  🎓 [{id}] learned from a quiet conversation; staged: {staged ?? "nothing"}");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  ✖ [{id}] learning failed: {ex.Message}");
                }
            }

            foreach (var job in sleepJobs.Values)
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
Console.WriteLine($"Memory:    sqlite hybrid (FTS5 + vector, RRF); budget {options.WorkingMemoryBudgetTokens} tok; personal {personalMemoryPath}");
Console.WriteLine($"Recall:    rewrite {(queryRewriter is null ? "off" : "on")}, rerank {(reranker is null ? "off" : "on")}, budget {attentionOptions.RecallBudgetTokens} tok");
Console.WriteLine($"Metacog:   {(attentionOptions.MetacognitionEnabled ? $"on (suppress after {attentionOptions.MetacognitionMinInjections} injections at ≤{attentionOptions.MetacognitionMaxUsefulRate:0.##} useful rate)" : "off")}");
Console.WriteLine($"Utility:   ledger with {utilityLedger.Count} event(s); judge {(utilityJudge is null ? "off (echo provider)" : "on")}");
Console.WriteLine($"Models:    main '{model}', background '{smallModel}'");
if (budget is not null)
    Console.WriteLine($"Budget:    {budget.Used:N0} of {budget.Limit:N0} tokens used{(budget.Exhausted ? ", spent" : "")}");
Console.WriteLine($"Sleep:     {(isEcho || sleepMinutes <= 0 ? "off" : $"every {sleepMinutes} min")}; " +
    $"pending proposals: {pendingConsolidations.List().Count}; ledger chain {(consolidationLedger.Verify() ? "OK" : "BROKEN")}");
Console.WriteLine($"Taskboard: {todos.Count} task(s)");
Console.WriteLine($"Tools:     {string.Join(", ", workspaceTools.All.Select(t => t.Name))}");
if (codeProfiles.Count > 0)
    Console.WriteLine(
        $"Code:      {string.Join(", ", codeProfiles.Values.Select(p => $"{p.Name} ({p.Workspace.Root})"))}; " +
        $"may run {string.Join(", ", config.CodeCommands)}");
Console.WriteLine($"Agent:     remember, search_memory, restore_memory + spawn_agent, delegate_task, list_agents, reply_to_agent, stop_agent");
Console.WriteLine($"Surfaces:  {string.Join(", ", surfaces.Select(s => s.Name))}");
Console.WriteLine($"Records:   transcripts in {transcriptDirectory}, host log in {logDirectory}");
Console.WriteLine($"Routing:   {decisionModel?.Name ?? "off (full pipeline every turn)"}");
Console.WriteLine($"Turns:     {(config.TurnTakingEnabled ? $"bursts (wait {turnTaking.DefaultWait.TotalSeconds:0.#}s until measured; cli immediate)" : "one message per turn")}");

await router.RunAsync(cts.Token);

Console.WriteLine("\nAgent stopped.");
