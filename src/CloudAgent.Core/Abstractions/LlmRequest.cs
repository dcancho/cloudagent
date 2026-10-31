using System.Collections.Generic;

namespace CloudAgent.Core.Abstractions;

/// <summary>
/// Everything a provider needs to produce one completion. This is the contract the
/// loop fills in each iteration; providers translate it to their own wire format.
/// </summary>
public sealed class LlmRequest
{
    /// <summary>
    /// Standing instructions / persona, sent out-of-band where the provider supports it. This
    /// is the <em>stable</em> part of the prompt — it changes rarely (persona, and memories that
    /// have earned permanent residence), so providers treat it as a cacheable prefix.
    /// </summary>
    public string? System { get; init; }

    /// <summary>
    /// Per-turn context that changes almost every turn — recalled memories, the rolling summary,
    /// the operational-state snapshot. Kept out of <see cref="System"/> on purpose: baking
    /// volatile text into the system prefix invalidates the prompt cache every turn. Providers
    /// place this at the tail (adjacent to the latest turn) so the stable prefix stays a cache
    /// hit. Framed as background reference, exactly as if it were the trailing system sections.
    /// </summary>
    public string? EphemeralContext { get; init; }

    /// <summary>The conversation so far, oldest first.</summary>
    public IReadOnlyList<Message> Messages { get; init; } = [];

    /// <summary>Model identifier. Provider-specific (e.g. "claude-opus-4-8").</summary>
    public string? Model { get; init; }

    /// <summary>What the call is for, see <see cref="LlmPurpose"/>. Used to account for spending; the model never sees it.</summary>
    public string? Purpose { get; init; }

    /// <summary>Upper bound on tokens to generate.</summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>Sampling temperature, 0..1.</summary>
    public double Temperature { get; init; } = 1.0;

    /// <summary>Tools the model may call this turn. Empty means a plain completion.</summary>
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];
}
