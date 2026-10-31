using System;
using System.Collections.Generic;

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

    /// <summary>The memory came back from Cold; its track record starts over from here.</summary>
    Restored,
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
    private static readonly MemoryUtilityStats None = new(nested: true);

    private readonly Dictionary<string, MemoryUtilityStats>? _byAgent;

    public MemoryUtilityStats() => _byAgent = [];

    private MemoryUtilityStats(bool nested)
    {
    }

    /// <summary>How many turns this memory was injected into.</summary>
    public int Injections { get; private set; }

    /// <summary>How many of those turns the judge attributed influence to it.</summary>
    public int Useful { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>When the memory last came back from Cold, if ever.</summary>
    public DateTimeOffset? RestoredAt { get; private set; }

    /// <summary>Fraction of injections that were judged useful, in [0, 1].</summary>
    public double UsefulRate => Injections == 0 ? 0 : (double)Useful / Injections;

    /// <summary>The same totals restricted to the events one agent produced: its own context.</summary>
    public MemoryUtilityStats InAgent(string agentId) =>
        _byAgent is not null && _byAgent.TryGetValue(agentId, out var stats) ? stats : None;

    internal void Apply(UtilityEvent utilityEvent)
    {
        if (_byAgent is not null)
        {
            if (utilityEvent.Signal == UtilitySignal.Restored)
                _byAgent.Clear();
            else
            {
                if (!_byAgent.TryGetValue(utilityEvent.AgentId, out var agentStats))
                    _byAgent[utilityEvent.AgentId] = agentStats = new MemoryUtilityStats(nested: true);
                agentStats.Apply(utilityEvent);
            }
        }

        switch (utilityEvent.Signal)
        {
            case UtilitySignal.Injected:
                Injections++;
                break;
            case UtilitySignal.JudgedUseful:
                Useful++;
                break;
            case UtilitySignal.Restored:
                Injections = 0;
                Useful = 0;
                RestoredAt = utilityEvent.At;
                break;
        }

        if (utilityEvent.At > LastSeenAt)
            LastSeenAt = utilityEvent.At;
    }
}
