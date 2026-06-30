using System.Collections.Generic;

namespace CloudAgent.Core.Abstractions;

/// <summary>
/// Everything a provider needs to produce one completion. This is the contract the
/// loop fills in each iteration; providers translate it to their own wire format.
/// </summary>
public sealed class LlmRequest
{
    /// <summary>Standing instructions / persona, sent out-of-band where the provider supports it.</summary>
    public string? System { get; init; }

    /// <summary>The conversation so far, oldest first.</summary>
    public IReadOnlyList<Message> Messages { get; init; } = [];

    /// <summary>Model identifier. Provider-specific (e.g. "claude-opus-4-8").</summary>
    public string? Model { get; init; }

    /// <summary>Upper bound on tokens to generate.</summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>Sampling temperature, 0..1.</summary>
    public double Temperature { get; init; } = 1.0;

    /// <summary>Tools the model may call this turn. Empty means a plain completion.</summary>
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];
}
