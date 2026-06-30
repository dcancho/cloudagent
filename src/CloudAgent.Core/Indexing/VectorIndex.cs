using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CloudAgent.Core.Indexing;

/// <summary>
/// An in-memory vector store with cosine-similarity search and JSON persistence. Small and
/// linear-scan by design — fine for a single agent's workspace; swap for a real vector DB
/// when the corpus outgrows memory.
/// </summary>
public sealed class VectorIndex
{
    private readonly List<IndexedChunk> _chunks = [];

    public int Count => _chunks.Count;

    public IReadOnlyList<IndexedChunk> Chunks => _chunks;

    public void Add(IndexedChunk chunk) => _chunks.Add(chunk);

    public void AddRange(IEnumerable<IndexedChunk> chunks) => _chunks.AddRange(chunks);

    public void Clear() => _chunks.Clear();

    /// <summary>Drop all chunks belonging to a source file (for re-indexing).</summary>
    public int RemoveSource(string source) =>
        _chunks.RemoveAll(c => string.Equals(c.Source, source, StringComparison.OrdinalIgnoreCase));

    /// <summary>The set of source files currently represented in the index.</summary>
    public IReadOnlyCollection<string> Sources =>
        _chunks.Select(c => c.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Return the <paramref name="topK"/> chunks most similar to <paramref name="query"/>.</summary>
    public IReadOnlyList<SearchHit> Search(float[] query, int topK = 5)
    {
        if (topK <= 0)
            return [];

        return _chunks
            .Select(c => new SearchHit(c, Cosine(query, c.Embedding)))
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToArray();
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0;

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            na += a[i] * (double)a[i];
            nb += b[i] * (double)b[i];
        }

        if (na <= 0 || nb <= 0)
            return 0;

        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, _chunks);
    }

    public static VectorIndex Load(string path)
    {
        var index = new VectorIndex();
        if (!File.Exists(path))
            return index;

        using var stream = File.OpenRead(path);
        var chunks = JsonSerializer.Deserialize<List<IndexedChunk>>(stream);
        if (chunks is not null)
            index._chunks.AddRange(chunks);
        return index;
    }
}
