using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Memory;

/// <summary>
/// Durable storage for memories, partitioned by <see cref="MemoryScope"/>. The seam lets
/// the default file-backed store be swapped for a database or vector service later.
/// </summary>
public interface IMemoryStore
{
    Task AddAsync(MemoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the memories most relevant to the query within the given scopes. Both forms of
    /// the query are provided so a store can search lexically (<paramref name="queryText"/>),
    /// semantically (<paramref name="queryVector"/>), or fuse the two; a vector-only store
    /// simply ignores the text.
    /// </summary>
    Task<IReadOnlyList<MemoryHit>> RecallAsync(
        string queryText,
        float[] queryVector,
        IReadOnlyCollection<MemoryScope> scopes,
        int topK,
        CancellationToken cancellationToken = default);

    Task ForgetAsync(string id, CancellationToken cancellationToken = default);

    int Count { get; }
}
