using CloudAgent.Core.Memory;

namespace CloudAgent.Core.Attention;

/// <summary>
/// Tuning knobs for the attention axis — what gets recalled and how much reaches the model each
/// turn. Kept separate from <see cref="Loop.AgentOptions"/> on purpose: these belong to the
/// attention module, and a future metacognitive gate (eje 8) will hang its own thresholds here
/// rather than polluting the host-wide options.
/// </summary>
public sealed class AttentionOptions
{
    /// <summary>Baseline number of memories to recall and inject each turn.</summary>
    public int RecallTopK { get; init; } = 3;

    /// <summary>Recall any memory scoring at or above this (cosine), even beyond the top-K.</summary>
    public double RecallThreshold { get; init; } = 0.80;

    /// <summary>
    /// Strict token budget for the injected memories block each turn — fewer, better memories
    /// beat many mediocre ones. 0 disables the cap. Converted to a char budget internally.
    /// </summary>
    public int RecallBudgetTokens { get; init; } = 400;

    /// <summary>Scopes consulted for recall (and for resident memories) each turn.</summary>
    public MemoryScope[] RecallScopes { get; init; } =
        [MemoryScope.Personal, MemoryScope.Project, MemoryScope.Session];

    // ---- Metacognition (eje 8) — the gate that suppresses low-track-record injections. ----

    /// <summary>Wrap the assembler in the metacognitive gate. Safe on: it is inert until the
    /// utility ledger has data, so it does nothing on a cold start.</summary>
    public bool MetacognitionEnabled { get; init; } = true;

    /// <summary>Track record (injections seen) a memory needs before the gate will judge it.</summary>
    public int MetacognitionMinInjections { get; init; } = 3;

    /// <summary>Suppress a judged memory whose useful rate is at or below this (0 = never helped).</summary>
    public double MetacognitionMaxUsefulRate { get; init; } = 0.0;
}
