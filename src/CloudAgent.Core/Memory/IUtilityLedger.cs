using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Memory;

/// <summary>
/// Append-only record of memory-utility events, with aggregate stats per memory. The ledger
/// is the source of truth — stats are always derivable by replaying it — so measurement stays
/// auditable: every promotion or forgetting decision can be traced back to raw events.
/// </summary>
public interface IUtilityLedger
{
    Task AppendAsync(IReadOnlyList<UtilityEvent> events, CancellationToken cancellationToken = default);

    /// <summary>Aggregate stats keyed by memory id, folded from all events seen so far.</summary>
    IReadOnlyDictionary<string, MemoryUtilityStats> Stats { get; }

    /// <summary>Total events recorded.</summary>
    int Count { get; }
}
