using CloudAgent.Core.Memory;
using CloudAgent.Core.Providers;

namespace CloudAgent.Core.Tests.Support;

/// <summary>
/// A real memory stack in a temporary directory: SQLite store for every scope, hash embeddings,
/// file-backed utility ledger and consolidation ledger. Deleted on dispose.
/// </summary>
public sealed class MemoryFixture : IDisposable
{
    public static readonly MemoryScope[] AllScopes = [MemoryScope.Personal, MemoryScope.Project, MemoryScope.Session];

    public MemoryFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "cloudagent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Store = new SqliteMemoryStore(new Dictionary<MemoryScope, string>
        {
            [MemoryScope.Personal] = Path.Combine(Root, "personal.db"),
            [MemoryScope.Project] = Path.Combine(Root, "project.db"),
            [MemoryScope.Session] = Path.Combine(Root, "session.db"),
        });
        Memory = new MemoryManager(Store, Embeddings);
        Utility = new FileUtilityLedger(UtilityPath);
        Ledger = new ConsolidationLedger(LedgerPath);
        Pending = new PendingConsolidations(Path.Combine(Root, "pending"));
    }

    public string Root { get; }

    public string UtilityPath => Path.Combine(Root, "utility.jsonl");

    public string LedgerPath => Path.Combine(Root, "consolidation.jsonl");

    public HashEmbeddingProvider Embeddings { get; } = new();

    public SqliteMemoryStore Store { get; }

    public MemoryManager Memory { get; }

    public FileUtilityLedger Utility { get; }

    public ConsolidationLedger Ledger { get; }

    public PendingConsolidations Pending { get; }

    public async Task<MemoryEntry> AddAsync(
        string content,
        MemoryScope scope = MemoryScope.Project,
        MemoryKind kind = MemoryKind.Fact,
        MemoryResidency residency = MemoryResidency.Active,
        DateTimeOffset? createdAt = null)
    {
        var vector = (await Embeddings.EmbedAsync([content]))[0];
        var entry = new MemoryEntry(Guid.NewGuid().ToString("N"), scope, kind, content, vector, 0.5,
            createdAt ?? DateTimeOffset.UtcNow, "test", [])
        {
            Residency = residency,
        };
        await Store.AddAsync(entry);
        return entry;
    }

    public Task<MemoryEntry?> FindAsync(string id) => Memory.FindAsync(id, AllScopes);

    /// <summary>Record <paramref name="injections"/> injections of a memory, of which <paramref name="useful"/> were judged useful.</summary>
    public Task RecordAsync(string id, int injections, int useful = 0, string agentId = "test")
    {
        var now = DateTimeOffset.UtcNow;
        var events = Enumerable.Range(0, injections)
            .SelectMany(i => new[]
            {
                new UtilityEvent(id, agentId, i, now, UtilitySignal.Injected, null),
                new UtilityEvent(id, agentId, i, now, i < useful ? UtilitySignal.JudgedUseful : UtilitySignal.JudgedUnused, null),
            })
            .ToArray();
        return Utility.AppendAsync(events);
    }

    public IReadOnlyList<ConsolidationRecord> ReadLedger() =>
        File.Exists(LedgerPath)
            ? File.ReadLines(LedgerPath).Select(line => System.Text.Json.JsonSerializer.Deserialize<ConsolidationRecord>(line)!).ToList()
            : [];

    public SleepJob Sleep(SleepOptions? options = null) =>
        new(new EchoLlmProvider(), Memory, Utility, Ledger, Pending, "test", "policy", options: options);

    public void Dispose()
    {
        Store.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind on Windows file locks does not affect the result.
        }
    }
}
