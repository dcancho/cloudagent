using System;

namespace CloudAgent.Core.Memory;

/// <summary>What a utility event says about an injected memory.</summary>
public enum UtilitySignal
{
    /// <summary>The memory was recalled and injected into the agent's context for a turn.</summary>
    Injected,

    /// <summary>The post-turn judge attributed influence on the reply to this memory.</summary>
    JudgedUseful,

    /// <summary>The post-turn judge saw no influence — the memory was ballast this turn.</summary>
    JudgedUnused,
}

/// <summary>
/// One observation about a memory's usefulness: it was injected, or it was judged after the
/// turn. Events are append-only; aggregate truth lives in <see cref="MemoryUtilityStats"/>.
/// This is the raw signal that later drives promotion, demotion, and forgetting.
/// </summary>
/// <param name="MemoryId">The <see cref="MemoryEntry.Id"/> this event is about.</param>
/// <param name="AgentId">Which agent's turn produced the event.</param>
/// <param name="Turn">The loop iteration within that agent's run.</param>
/// <param name="At">When the event was recorded.</param>
/// <param name="Signal">What happened.</param>
/// <param name="Evidence">Short free-form support, e.g. the recall score.</param>
public sealed record UtilityEvent(
    string MemoryId,
    string AgentId,
    int Turn,
    DateTimeOffset At,
    UtilitySignal Signal,
    string? Evidence);

/// <summary>
/// Running totals for one memory, folded from its events. The useful rate is the signal the
/// consolidation passes read: high → promote, chronically low → demote or forget.
/// </summary>
public sealed class MemoryUtilityStats
{
    /// <summary>How many turns this memory was injected into.</summary>
    public int Injections { get; private set; }

    /// <summary>How many of those turns the judge attributed influence to it.</summary>
    public int Useful { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>Fraction of injections that were judged useful, in [0, 1].</summary>
    public double UsefulRate => Injections == 0 ? 0 : (double)Useful / Injections;

    internal void Apply(UtilityEvent utilityEvent)
    {
        switch (utilityEvent.Signal)
        {
            case UtilitySignal.Injected:
                Injections++;
                break;
            case UtilitySignal.JudgedUseful:
                Useful++;
                break;
        }

        if (utilityEvent.At > LastSeenAt)
            LastSeenAt = utilityEvent.At;
    }
}
