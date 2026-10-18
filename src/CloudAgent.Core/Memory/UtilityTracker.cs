using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Memory;

/// <summary>
/// The policy layer of the utility feedback loop, one per agent: records which memories were
/// injected each turn, and after the turn asks the <see cref="UtilityJudge"/> to attribute
/// influence, writing both signals to the shared ledger. Closes the loop that everything
/// else (decay, promotion, forgetting) will read from.
/// </summary>
public sealed class UtilityTracker(IUtilityLedger ledger, UtilityJudge? judge, string agentId)
{
    public IUtilityLedger Ledger => ledger;

    /// <summary>Record that these memories were injected into the given turn.</summary>
    public Task RecordInjectionAsync(
        IReadOnlyList<MemoryHit> injected, int turn, CancellationToken cancellationToken = default)
    {
        if (injected.Count == 0)
            return Task.CompletedTask;

        var now = DateTimeOffset.UtcNow;
        var events = injected
            .Select(hit => new UtilityEvent(
                hit.Entry.Id, agentId, turn, now, UtilitySignal.Injected, $"score={hit.Score:F2}"))
            .ToArray();

        return ledger.AppendAsync(events, cancellationToken);
    }

    /// <summary>
    /// Judge the finished turn and record a useful/unused verdict per injected memory.
    /// Returns how many were useful, or null when no judge is configured or it gave no verdict; then
    /// nothing is recorded, because a failed judgment is not evidence that a memory was unused.
    /// </summary>
    public async Task<int?> JudgeTurnAsync(
        IReadOnlyList<MemoryHit> injected,
        string? userText,
        string assistantText,
        int turn,
        CancellationToken cancellationToken = default)
    {
        if (judge is null || injected.Count == 0)
            return null;

        var entries = injected.Select(hit => hit.Entry).ToArray();
        var verdict = await judge
            .JudgeAsync(entries, userText, assistantText, cancellationToken)
            .ConfigureAwait(false);
        if (verdict is null)
            return null;

        var useful = verdict.ToHashSet();

        var now = DateTimeOffset.UtcNow;
        var events = entries
            .Select((entry, i) => new UtilityEvent(
                entry.Id, agentId, turn, now,
                useful.Contains(i) ? UtilitySignal.JudgedUseful : UtilitySignal.JudgedUnused,
                null))
            .ToArray();

        await ledger.AppendAsync(events, cancellationToken).ConfigureAwait(false);
        return useful.Count;
    }
}
