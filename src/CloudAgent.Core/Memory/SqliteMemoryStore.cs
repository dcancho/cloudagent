using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Indexing;
using Microsoft.Data.Sqlite;

namespace CloudAgent.Core.Memory;

/// <summary>
/// Hybrid SQLite-backed memory store, one database file per scope (so Personal can stay
/// user-global while Project/Session live in the workspace). Recall fuses two legs with
/// reciprocal rank fusion:
/// <list type="bullet">
/// <item>lexical — FTS5 over content, which catches exact names, commands, identifiers;</item>
/// <item>semantic — cosine over stored embeddings, which catches the fuzzy rest.</item>
/// </list>
/// Reads go to the database every time (always fresh across concurrent agents); WAL mode +
/// busy timeout make cross-agent access safe where the JSON store would corrupt. Reported
/// <see cref="MemoryHit.Score"/> stays cosine similarity — ordering is hybrid, the score's
/// meaning is unchanged for thresholds and the utility ledger.
/// </summary>
public sealed class SqliteMemoryStore : IMemoryStore, IDisposable
{
    /// <summary>Standard reciprocal-rank-fusion constant (Cormack et al.).</summary>
    private const int RrfK = 60;

    /// <summary>How many candidates each leg contributes before fusion.</summary>
    private const int CandidatesPerLeg = 50;

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly Dictionary<MemoryScope, SqliteConnection> _connections = [];

    public SqliteMemoryStore(IReadOnlyDictionary<MemoryScope, string> scopePaths)
    {
        foreach (var (scope, path) in scopePaths)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
            Execute(connection,
                """
                CREATE TABLE IF NOT EXISTS memories (
                    id TEXT PRIMARY KEY,
                    scope TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    content TEXT NOT NULL,
                    embedding BLOB NOT NULL,
                    importance REAL NOT NULL,
                    created_at TEXT NOT NULL,
                    source TEXT,
                    links TEXT NOT NULL,
                    metadata TEXT NOT NULL
                );
                CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(content, id UNINDEXED);
                """);

            _connections[scope] = connection;
        }
    }

    public int Count => _connections.Values.Sum(c => Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM memories")));

    public Task AddAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(entry.Scope, out var connection))
            throw new InvalidOperationException($"No storage path configured for scope '{entry.Scope}'.");

        using var transaction = connection.BeginTransaction();

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO memories (id, scope, kind, content, embedding, importance, created_at, source, links, metadata)
            VALUES ($id, $scope, $kind, $content, $embedding, $importance, $createdAt, $source, $links, $metadata)
            """;
        insert.Parameters.AddWithValue("$id", entry.Id);
        insert.Parameters.AddWithValue("$scope", entry.Scope.ToString());
        insert.Parameters.AddWithValue("$kind", entry.Kind.ToString());
        insert.Parameters.AddWithValue("$content", entry.Content);
        insert.Parameters.AddWithValue("$embedding", MemoryMarshal.AsBytes<float>(entry.Embedding).ToArray());
        insert.Parameters.AddWithValue("$importance", entry.Importance);
        insert.Parameters.AddWithValue("$createdAt", entry.CreatedAt.ToString("O"));
        insert.Parameters.AddWithValue("$source", (object?)entry.Source ?? DBNull.Value);
        insert.Parameters.AddWithValue("$links", JsonSerializer.Serialize(entry.Links, JsonOptions));
        insert.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(entry.Metadata, JsonOptions));
        insert.ExecuteNonQuery();

        using var fts = connection.CreateCommand();
        fts.Transaction = transaction;
        fts.CommandText = "INSERT INTO memories_fts (content, id) VALUES ($content, $id)";
        fts.Parameters.AddWithValue("$content", entry.Content);
        fts.Parameters.AddWithValue("$id", entry.Id);
        fts.ExecuteNonQuery();

        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryHit>> RecallAsync(
        string queryText,
        float[] queryVector,
        IReadOnlyCollection<MemoryScope> scopes,
        int topK,
        CancellationToken cancellationToken = default)
    {
        if (topK <= 0)
            return Task.FromResult<IReadOnlyList<MemoryHit>>([]);

        var active = scopes.Where(_connections.ContainsKey).ToArray();

        // Semantic leg: cosine over every entry in scope (linear scan, same as the rest of
        // the harness — fine until the corpus outgrows memory).
        var entries = new Dictionary<string, MemoryEntry>();
        foreach (var scope in active)
        {
            foreach (var entry in LoadScope(_connections[scope]))
                entries[entry.Id] = entry;
        }

        var cosineById = entries.Values.ToDictionary(
            e => e.Id, e => VectorIndex.Cosine(queryVector, e.Embedding));

        var semanticLeg = cosineById
            .OrderByDescending(pair => pair.Value)
            .Take(CandidatesPerLeg)
            .Select(pair => pair.Key)
            .ToArray();

        // Lexical leg: FTS5 (BM25) per scope, merged on raw BM25 (lower is better). Scores
        // from different files aren't strictly comparable, but for fusion ranks it's fine.
        var lexical = new List<(string Id, double Bm25)>();
        var match = BuildMatch(queryText);
        if (match is not null)
        {
            foreach (var scope in active)
                lexical.AddRange(FtsSearch(_connections[scope], match));
        }

        var lexicalLeg = lexical
            .OrderBy(hit => hit.Bm25)
            .Take(CandidatesPerLeg)
            .Select(hit => hit.Id)
            .ToArray();

        // Reciprocal rank fusion: score = Σ 1 / (k + rank) over the legs an id appears in.
        var fused = new Dictionary<string, double>();
        Fuse(fused, semanticLeg);
        Fuse(fused, lexicalLeg);

        var hits = fused
            .OrderByDescending(pair => pair.Value)
            .Take(topK)
            .Select(pair => new MemoryHit(entries[pair.Key], cosineById[pair.Key]))
            .ToArray();

        return Task.FromResult<IReadOnlyList<MemoryHit>>(hits);
    }

    public Task ForgetAsync(string id, CancellationToken cancellationToken = default)
    {
        foreach (var connection in _connections.Values)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM memories WHERE id = $id; DELETE FROM memories_fts WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var connection in _connections.Values)
            connection.Dispose();
    }

    private static void Fuse(Dictionary<string, double> fused, IReadOnlyList<string> leg)
    {
        for (var rank = 0; rank < leg.Count; rank++)
            fused[leg[rank]] = fused.GetValueOrDefault(leg[rank]) + 1.0 / (RrfK + rank + 1);
    }

    /// <summary>
    /// Turn free text into a safe FTS5 MATCH expression: quoted tokens OR-ed together.
    /// Null when the text has nothing searchable (the lexical leg is skipped).
    /// </summary>
    private static string? BuildMatch(string queryText)
    {
        var tokens = Regex.Matches(queryText, @"[\p{L}\p{Nd}_]+")
            .Select(m => m.Value)
            .Where(t => t.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

        return tokens.Length == 0 ? null : string.Join(" OR ", tokens.Select(t => $"\"{t}\""));
    }

    private static IEnumerable<(string Id, double Bm25)> FtsSearch(SqliteConnection connection, string match)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT id, bm25(memories_fts) FROM memories_fts WHERE memories_fts MATCH $match ORDER BY rank LIMIT {CandidatesPerLeg}";
        command.Parameters.AddWithValue("$match", match);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return (reader.GetString(0), reader.GetDouble(1));
    }

    private static IEnumerable<MemoryEntry> LoadScope(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, scope, kind, content, embedding, importance, created_at, source, links, metadata FROM memories";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var embeddingBytes = (byte[])reader.GetValue(4);
            var embedding = new float[embeddingBytes.Length / sizeof(float)];
            Buffer.BlockCopy(embeddingBytes, 0, embedding, 0, embeddingBytes.Length);

            yield return new MemoryEntry(
                reader.GetString(0),
                Enum.Parse<MemoryScope>(reader.GetString(1)),
                Enum.Parse<MemoryKind>(reader.GetString(2)),
                reader.GetString(3),
                embedding,
                reader.GetDouble(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(8), JsonOptions) ?? [])
            {
                Metadata = JsonSerializer
                    .Deserialize<Dictionary<string, string>>(reader.GetString(9), JsonOptions) ?? [],
            };
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }
}
