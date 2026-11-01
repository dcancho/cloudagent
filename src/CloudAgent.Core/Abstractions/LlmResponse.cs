using System.Collections.Generic;

namespace CloudAgent.Core.Abstractions;

/// <summary>
/// Why the provider stopped generating.
/// </summary>
public enum StopReason
{
    /// <summary>The model finished its turn naturally.</summary>
    EndTurn,

    /// <summary>Generation hit <see cref="LlmRequest.MaxTokens"/>.</summary>
    MaxTokens,

    /// <summary>Some other / provider-specific reason.</summary>
    Other,
}

/// <summary>
/// The result of a single completion.
/// </summary>
/// <param name="Text">The assistant's generated text.</param>
/// <param name="StopReason">Why generation ended.</param>
/// <param name="InputTokens">Tokens consumed by the prompt, if reported. On providers with
/// prompt caching this includes the cached ones, so cache reads and writes are always a part of it.</param>
/// <param name="OutputTokens">Tokens generated, if reported.</param>
public sealed record LlmResponse(
    string Text,
    StopReason StopReason = StopReason.EndTurn,
    int? InputTokens = null,
    int? OutputTokens = null)
{
    /// <summary>Tools the model asked to run this turn, if any.</summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>True when the model requested at least one tool call.</summary>
    public bool HasToolCalls => ToolCalls.Count > 0;

    /// <summary>Prompt tokens served from cache this turn (cheap), if the provider reports them.</summary>
    public int? CacheReadTokens { get; init; }

    /// <summary>Prompt tokens written to cache this turn (a one-time premium), if reported.</summary>
    public int? CacheWriteTokens { get; init; }
}
