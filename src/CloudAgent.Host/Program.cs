using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Loop;
using CloudAgent.Core.Indexing;
using CloudAgent.Core.Memory;
using CloudAgent.Core.Providers;
using CloudAgent.Core.Surfaces;
using CloudAgent.Core.Tools;
using CloudAgent.Core.Tools.Index;
using CloudAgent.Core.Tools.Memory;
using CloudAgent.Core.Tools.Todos;
using CloudAgent.Core.Tools.Web;
using CloudAgent.Core.Todos;
using CloudAgent.Core.Workspaces;
using CloudAgent.Host;

// ---- Wiring -----------------------------------------------------------------
// Pick a provider by what's configured, falling back to the offline echo provider
// so the harness always runs out of the box:
//   1. Foundry   — FOUNDRY_ENDPOINT (+ optional FOUNDRY_API_KEY / FOUNDRY_MODEL)
//   2. Anthropic — ANTHROPIC_API_KEY
//   3. Echo      — no config
var foundryEndpoint = Environment.GetEnvironmentVariable("FOUNDRY_ENDPOINT");
var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

string model = "claude-opus-4-8";
ILlmProvider provider;

if (!string.IsNullOrWhiteSpace(foundryEndpoint))
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
var summarizer = new Summarizer(provider, model);

// The agent's toolbox, scoped to the workspace.
var tools = Toolset.CreateDefault(workspace, search)
    .Register(new IndexWorkspaceTool(indexer, index, indexPath))
    .Register(new SearchIndexTool(embeddings, index))
    .Register(new SummarizeFileTool(workspace, summarizer));

// Memory: Personal is user-global (follows the agent across projects); Project and Session
// live in the workspace. A cheap local embedder keeps episodic archival/recall free.
var sessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
var personalMemoryPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".cloudagent", "memory", "personal.json");
var memoryStore = new FileMemoryStore(new Dictionary<MemoryScope, string>
{
    [MemoryScope.Personal] = personalMemoryPath,
    [MemoryScope.Project] = Path.Combine(workspace.Root, ".agent", "memory", "project.json"),
    [MemoryScope.Session] = Path.Combine(workspace.Root, ".agent", "memory", "sessions", $"{sessionId}.json"),
});
var memory = new MemoryManager(memoryStore, new HashEmbeddingProvider());
tools.Register(new RememberTool(memory));

// Taskboard: a persistent todo list in the workspace.
var todos = new TodoStore(Path.Combine(workspace.Root, ".agent", "todos.json"));
tools
    .Register(new TodoListTool(todos))
    .Register(new TodoAddTool(todos))
    .Register(new TodoCompleteTool(todos))
    .Register(new TodoRemoveTool(todos))
    .Register(new TodoClearTool(todos));

var budget = int.TryParse(Environment.GetEnvironmentVariable("AGENT_MEMORY_BUDGET_TOKENS"), out var b)
    ? b
    : 8000;

var options = new AgentOptions
{
    SystemPrompt =
        "You are an autonomous agent running on your own machine. You have a persistent " +
        "workspace and operate with the initiative of a capable colleague. Be concise.",
    Model = model,
    IterationDelay = TimeSpan.Zero, // interactive: no idle delay between turns
    WorkingMemoryBudgetTokens = budget,
};

// Consolidation: extract durable facts from turns as they're compacted away (uses the chat LLM).
var consolidator = new Consolidator(provider, memory, model);
var compactor = new ContextCompactor(summarizer, options, memory, consolidator);

// Ctrl+C or typing "exit" stops everything cleanly.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Each conversation (surface + chat id) becomes its own agent session. Provider, tools,
// workspace, memory and compactor are shared; the conversation gets its own context + input.
AgentConversation CreateConversation(string surface, string conversationId)
{
    var queue = new QueueInputSource();
    var context = new AgentContext();
    var loop = new AgentLoop(provider, options, queue, context, tools, compactor, memory);

    loop.ToolInvoked += (call, result) =>
        Console.WriteLine($"  ⚙ [{surface}:{conversationId}] {call.Name} -> {(result.Success ? "ok" : "error")}");
    loop.Compacted += evicted =>
        Console.WriteLine($"  🧠 [{surface}:{conversationId}] compacted {evicted} turn(s)");
    loop.MemoriesRecalled += count =>
        Console.WriteLine($"  🧠 [{surface}:{conversationId}] recalled {count} memor(ies)");
    loop.Consolidated += count =>
        Console.WriteLine($"  🧠 [{surface}:{conversationId}] consolidated {count} fact(s)");

    return new AgentConversation(queue, loop);
}

// Surfaces: the console is always on; Telegram joins if a bot token is present.
var surfaces = new List<ISurface> { new CliSurface(cts) };
var telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
if (!string.IsNullOrWhiteSpace(telegramToken))
    surfaces.Add(new TelegramSurface(telegramToken));

var router = new ConversationRouter(surfaces, CreateConversation);
router.ConversationOpened += (s, c) => Console.WriteLine($"  ✉ conversation opened: {s}:{c}");

// ---- Run --------------------------------------------------------------------
Console.WriteLine($"CloudAgent harness — provider: {provider.Name}");
Console.WriteLine($"Workspace: {workspace.Root}");
Console.WriteLine($"Search:    {search.Name}");
Console.WriteLine($"Embeddings:{embeddings.Name} (index: {index.Count} chunk(s))");
Console.WriteLine($"Memory:    {memoryStore.Count} entr(ies); budget {options.WorkingMemoryBudgetTokens} tok; session {sessionId}");
Console.WriteLine($"Taskboard: {todos.Count} task(s)");
Console.WriteLine($"Tools:     {string.Join(", ", tools.All.Select(t => t.Name))}");
Console.WriteLine($"Surfaces:  {string.Join(", ", surfaces.Select(s => s.Name))}");

await router.RunAsync(cts.Token);

Console.WriteLine("\nAgent stopped.");
