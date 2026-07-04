using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Indexing;

namespace CloudAgent.Core.Memory;

/// <summary>
/// A file-backed memory store: one JSON file per scope. Personal memory typically points at
/// a user-global path (so it follows the agent across projects), while Project and Session
/// live inside the workspace. Cosine search reuses the index's vector math. Single-agent,
/// single-threaded by assumption — no locking.
/// </summary>
public sealed class FileMemoryStore : IMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<MemoryScope, string> _paths;
    private readonly Dictionary<MemoryScope, List<MemoryEntry>> _byScope = [];

    public FileMemoryStore(IReadOnlyDictionary<MemoryScope, string> scopePaths)
    {
        _paths = new Dictionary<MemoryScope, string>(scopePaths);
        foreach (var (scope, path) in _paths)
            _byScope[scope] = Load(path);
    }

    public int Count => _byScope.Values.Sum(list => list.Count);

    public Task AddAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        if (!_byScope.TryGetValue(entry.Scope, out var list))
            throw new InvalidOperationException($"No storage path configured for scope '{entry.Scope}'.");

        list.Add(entry);
        Save(entry.Scope);
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

        var hits = scopes
            .Where(_byScope.ContainsKey)
            .SelectMany(scope => _byScope[scope])
            .Where(entry => entry.Residency == MemoryResidency.Active)
            .Select(entry => new MemoryHit(entry, VectorIndex.Cosine(queryVector, entry.Embedding)))
            .OrderByDescending(hit => hit.Score)
            .Take(topK)
            .ToArray();

        return Task.FromResult<IReadOnlyList<MemoryHit>>(hits);
    }

    public Task<IReadOnlyList<MemoryEntry>> ListAsync(
        IReadOnlyCollection<MemoryScope> scopes, CancellationToken cancellationToken = default)
    {
        var entries = scopes
            .Where(_byScope.ContainsKey)
            .SelectMany(scope => _byScope[scope])
            .ToArray();
        return Task.FromResult<IReadOnlyList<MemoryEntry>>(entries);
    }

    public Task UpdateAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        if (!_byScope.TryGetValue(entry.Scope, out var list))
            throw new InvalidOperationException($"No storage path configured for scope '{entry.Scope}'.");

        var index = list.FindIndex(e => e.Id == entry.Id);
        if (index < 0)
            throw new InvalidOperationException($"No memory with id '{entry.Id}' in scope '{entry.Scope}'.");

        list[index] = entry;
        Save(entry.Scope);
        return Task.CompletedTask;
    }

    public Task ForgetAsync(string id, CancellationToken cancellationToken = default)
    {
        foreach (var (scope, list) in _byScope)
        {
            if (list.RemoveAll(e => e.Id == id) > 0)
                Save(scope);
        }
        return Task.CompletedTask;
    }

    private static List<MemoryEntry> Load(string path)
    {
        if (!File.Exists(path))
            return [];

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<MemoryEntry>>(stream, JsonOptions) ?? [];
    }

    private void Save(MemoryScope scope)
    {
        var path = _paths[scope];
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, _byScope[scope], JsonOptions);
    }
}
