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

    /// <summary>Return the closest memories to <paramref name="query"/> within the given scopes.</summary>
    Task<IReadOnlyList<MemoryHit>> RecallAsync(
        float[] query,
        IReadOnlyCollection<MemoryScope> scopes,
        int topK,
        CancellationToken cancellationToken = default);

    Task ForgetAsync(string id, CancellationToken cancellationToken = default);

    int Count { get; }
}
