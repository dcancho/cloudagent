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
using CloudAgent.Host;

// ---- Wiring -----------------------------------------------------------------
// Pick a provider by what's configured, falling back to the offline echo provider
// so the harness always runs out of the box. Foundry is the production choice (one
// endpoint serves the whole catalog; the model is picked per request, so background
// jobs can run on a cheap model via AGENT_SMALL_MODEL):
//   1. Claude CLI — CLAUDE_CLI=1 (explicit opt-in; rides Pro/Max via `claude -p`)
//   2. Foundry    — FOUNDRY_ENDPOINT (+ optional FOUNDRY_API_KEY / FOUNDRY_MODEL)
//   3. Anthropic  — ANTHROPIC_API_KEY
//   4. Echo       — no config
var foundryEndpoint = Environment.GetEnvironmentVariable("FOUNDRY_ENDPOINT");
var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
var useClaudeCli = string.Equals(Environment.GetEnvironmentVariable("CLAUDE_CLI"), "1", StringComparison.Ordinal);

string model = "claude-opus-4-8";
ILlmProvider provider;

if (useClaudeCli)
{
    var claudeModel = Environment.GetEnvironmentVariable("CLAUDE_MODEL"); // null => Claude Code default
    provider = new ClaudeCliProvider(claudeModel);
    model = claudeModel ?? ""; // empty => provider omits --model (uses the account's default)
}
else if (!string.IsNullOrWhiteSpace(foundryEndpoint))
{
    model = Environment.GetEnvironmentVariable("FOUNDRY_MODEL") ?? "gpt-4o-mini";
    provider = new FoundryLlmProvider(
        new Uri(foundryEndpoint),
        Environment.GetEnvironmentVariable("FOUNDRY_API_KEY"),
        model);
}
else if (!string.IsNullOrWhiteSpace(anthropicKey))
{
    provider = new AnthropicLlmProvider(anthropicKey);
}
else
{
    provider = new EchoLlmProvider();
}

// The agent's world: a rooted area it may act within (env AGENT_WORKSPACE, else ./workspace).
var workspaceRoot = Environment.GetEnvironmentVariable("AGENT_WORKSPACE")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "workspace");
IWorkspace workspace = new Workspace(workspaceRoot);

// Web search backend: Tavily if a key is present, otherwise unconfigured.
var tavilyKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY");
IWebSearchProvider search = string.IsNullOrWhiteSpace(tavilyKey)
    ? new NullWebSearchProvider()
    : new TavilyWebSearchProvider(tavilyKey);

// Embeddings backend: Foundry if its endpoint is set, else a deterministic offline hash.
IEmbeddingProvider embeddings = !string.IsNullOrWhiteSpace(foundryEndpoint)
    ? new FoundryEmbeddingProvider(
        new Uri(foundryEndpoint),
        Environment.GetEnvironmentVariable("FOUNDRY_API_KEY"),
        Environment.GetEnvironmentVariable("FOUNDRY_EMBED_MODEL") ?? "text-embedding-3-small")
    : new HashEmbeddingProvider();

// Semantic index over the workspace, persisted under .agent/index.json.
var indexPath = Path.Combine(workspace.Root, ".agent", "index.json");
var index = VectorIndex.Load(indexPath);
var indexer = new WorkspaceIndexer(workspace, embeddings, index);

// Background jobs (summaries, consolidation, utility judging, state snapshots) run on a
// cheap catalog model when one is configured; the main loop keeps the big one.
var smallModel = Environment.GetEnvironmentVariable("AGENT_SMALL_MODEL") ?? model;
var summarizer = new Summarizer(provider, smallModel);

// Workspace tools shared by every agent — they all operate on the same workspace.
var workspaceTools = Toolset.CreateDefault(workspace, search)
    .Register(new IndexWorkspaceTool(indexer, index, indexPath))
    .Register(new SearchIndexTool(embeddings, index))
    .Register(new SummarizeFileTool(workspace, summarizer));

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
var utilityJudge = provider is EchoLlmProvider ? null : new UtilityJudge(provider, smallModel);
var snapshotter = provider is EchoLlmProvider ? null : new StateSnapshotter(provider, smallModel);

// Read-side pipeline: rewrite the query before searching, rerank the wide hybrid pass down
// to the few genuinely useful memories. Both on the small model; off under echo.
var queryRewriter = provider is EchoLlmProvider ? null : new QueryRewriter(provider, smallModel);
var reranker = provider is EchoLlmProvider ? null : new MemoryReranker(provider, smallModel);

var budget = int.TryParse(Environment.GetEnvironmentVariable("AGENT_MEMORY_BUDGET_TOKENS"), out var b)
    ? b
    : 8000;

var options = new AgentOptions
{
    SystemPrompt =
        "You are an autonomous agent running on your own machine with a persistent workspace. " +
        "Operate with the initiative of a capable colleague. You can delegate scoped work to child " +
        "agents (spawn_agent / delegate_task) and answer their questions. Be concise.",
    Model = model,
    IterationDelay = TimeSpan.Zero,
    WorkingMemoryBudgetTokens = budget,
};

var childOptions = new AgentOptions
{
    SystemPrompt =
        "You are a subagent working on a scoped task from your parent. You have your own memory and " +
        "the shared workspace. Use ask_parent for a decision you can't make; call finish with your " +
        "result when done. Be concise.",
    Model = model,
    IterationDelay = TimeSpan.Zero,
    WorkingMemoryBudgetTokens = budget,
};

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
    return (mem, comp, utility, retriever);
}

void AttachLogging(AgentLoop loop, string label)
{
    loop.ToolInvoked += (call, result) =>
        Console.WriteLine($"  ⚙ [{label}] {call.Name} -> {(result.Success ? "ok" : "error")}");
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
    var loop = new AgentLoop(provider, childOptions, input, new AgentContext(), childTools, comp, mem, utility, retriever);
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
        .Register(new SpawnAgentTool(orchestrator, handle))
        .Register(new DelegateTaskTool(orchestrator, handle))
        .Register(new ListAgentsTool(bus, handle))
        .Register(new ReplyToAgentTool(bus, handle))
        .Register(new StopAgentTool(bus));

    var loop = new AgentLoop(provider, options, input, new AgentContext(), agentTools, comp, mem, utility, retriever);
    AttachLogging(loop, handle.Id);
    return new AgentConversation(queue, loop);
}

// Surfaces: the console is always on; others join when configured.
var surfaces = new List<ISurface> { new CliSurface(cts) };

var telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
if (!string.IsNullOrWhiteSpace(telegramToken))
    surfaces.Add(new TelegramSurface(telegramToken));

var discordToken = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
if (!string.IsNullOrWhiteSpace(discordToken))
    surfaces.Add(new DiscordSurface(discordToken));

if (string.Equals(Environment.GetEnvironmentVariable("WEBCHAT"), "1", StringComparison.Ordinal))
{
    var webPort = int.TryParse(Environment.GetEnvironmentVariable("WEBCHAT_PORT"), out var wp) ? wp : 5080;
    var webchat = new WebchatSurface(webPort);
    surfaces.Add(webchat);
    Console.WriteLine($"Web chat:  {webchat.Url}");
}

var router = new ConversationRouter(surfaces, CreateConversation);
router.ConversationOpened += (s, c) => Console.WriteLine($"  ✉ conversation opened: {s}:{c}");

// ---- Run --------------------------------------------------------------------
Console.WriteLine($"CloudAgent harness — provider: {provider.Name}");
Console.WriteLine($"Workspace: {workspace.Root}");
Console.WriteLine($"Search:    {search.Name}");
Console.WriteLine($"Embeddings:{embeddings.Name} (index: {index.Count} chunk(s))");
Console.WriteLine($"Memory:    sqlite hybrid (FTS5 + vector, RRF); budget {options.WorkingMemoryBudgetTokens} tok");
Console.WriteLine($"Recall:    rewrite {(queryRewriter is null ? "off" : "on")}, rerank {(reranker is null ? "off" : "on")}, budget {options.RecallBudgetTokens} tok");
Console.WriteLine($"Utility:   ledger with {utilityLedger.Count} event(s); judge {(utilityJudge is null ? "off (echo provider)" : "on")}");
Console.WriteLine($"Models:    main '{model}', background '{smallModel}'");
Console.WriteLine($"Taskboard: {todos.Count} task(s)");
Console.WriteLine($"Tools:     {string.Join(", ", workspaceTools.All.Select(t => t.Name))}");
Console.WriteLine($"Agent:     remember + spawn_agent, delegate_task, list_agents, reply_to_agent, stop_agent");
Console.WriteLine($"Surfaces:  {string.Join(", ", surfaces.Select(s => s.Name))}");

await router.RunAsync(cts.Token);

Console.WriteLine("\nAgent stopped.");
