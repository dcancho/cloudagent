using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Loop;
using CloudAgent.Core.Memory;

namespace CloudAgent.Core.Attention;

/// <summary>
/// The metacognitive gate (eje 8): a decorator over any <see cref="IAttentionManager"/> that
/// suppresses injected memories whose measured track record says they keep not applying — the
/// "looked familiar, didn't help" failure. It reasons over the utility ledger (eje 7's output),
/// not over cosine similarity: similarity measures familiarity, which is exactly the half that
/// fools you; the ledger measures the familiarity-vs-usefulness gap directly.
///
/// This is read-time, per-turn, and reversible: a memory suppressed this turn is untouched in the
/// store (unlike the sleep job's demotion to Cold). Inert until the ledger has data, so it does
/// nothing on a cold start and only bites once measurement accumulates.
/// </summary>
public sealed class MetacognitiveAttentionManager(
    IAttentionManager inner,
    IUtilityLedger utility,
    AttentionOptions options,
    Action<MemoryHit>? onSuppressed = null) : IAttentionManager
{
    public async Task<AssembledContext> AssembleAsync(
        AgentContext context, string? query, MemoryLevel memory = MemoryLevel.Full,
        CancellationToken cancellationToken = default)
    {
        var assembled = await inner.AssembleAsync(context, query, memory, cancellationToken).ConfigureAwait(false);
        if (assembled.InjectedMemories.Count == 0)
            return assembled;

        var stats = utility.Stats;
        var kept = new List<MemoryHit>(assembled.InjectedMemories.Count);
        foreach (var hit in assembled.InjectedMemories)
        {
            if (IsLowConfidence(stats, hit.Entry.Id))
                onSuppressed?.Invoke(hit);
            else
                kept.Add(hit);
        }

        // Nothing failed the gate — leave the inner assembly exactly as it was.
        if (kept.Count == assembled.InjectedMemories.Count)
            return assembled;

        // Some were suppressed — re-render the volatile block over the survivors (state and
        // summary are untouched; only the memory section shrinks).
        var ephemeral = AttentionRendering.Ephemeral(
            context.State.Render(), context.RollingSummary, kept, assembled.ColdStubs, options.ColdStubPreviewChars);
        return assembled with { EphemeralContext = ephemeral, InjectedMemories = kept };
    }

    /// <summary>
    /// Low confidence = the memory has been injected often enough to judge and has (almost) never
    /// helped. Same shape as the sleep job's fact-demotion criterion, but applied live per turn.
    /// A memory with no track record yet is never suppressed (nothing to be skeptical about).
    /// </summary>
    private bool IsLowConfidence(IReadOnlyDictionary<string, MemoryUtilityStats> stats, string memoryId)
    {
        var stat = stats.GetValueOrDefault(memoryId);
        return stat is not null
            && stat.Injections >= options.MetacognitionMinInjections
            && stat.UsefulRate <= options.MetacognitionMaxUsefulRate;
    }
}
