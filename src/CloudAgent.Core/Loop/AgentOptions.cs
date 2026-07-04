using System;

namespace CloudAgent.Core.Loop;

/// <summary>
/// Tuning knobs for a single agent's run.
/// </summary>
public sealed class AgentOptions
{
    /// <summary>The agent's standing instructions / persona.</summary>
    public string SystemPrompt { get; init; } =
        "You are an autonomous agent with your own workspace. You think, act, and learn over time.";

    /// <summary>Model identifier passed through to the provider.</summary>
    public string Model { get; init; } = "claude-opus-4-8";

    public int MaxTokens { get; init; } = 4096;

    public double Temperature { get; init; } = 1.0;

    /// <summary>Stop after this many iterations. 0 means run until cancelled.</summary>
    public int MaxIterations { get; init; } = 0;

    /// <summary>Pause between iterations, so an idle loop doesn't spin hot.</summary>
    public TimeSpan IterationDelay { get; init; } = TimeSpan.FromSeconds(1);

    // ---- Memory ----

    /// <summary>Compact working memory once its estimated tokens exceed this. User-tunable.</summary>
    public int WorkingMemoryBudgetTokens { get; init; } = 8000;

    /// <summary>How many recent messages to keep verbatim when compacting.</summary>
    public int KeepRecentMessages { get; init; } = 12;

    /// <summary>Baseline number of memories to recall and inject each turn.</summary>
    public int RecallTopK { get; init; } = 3;

    /// <summary>Recall any memory scoring at or above this (cosine), even beyond the top-K.</summary>
    public double RecallThreshold { get; init; } = 0.80;

    /// <summary>
    /// Strict token budget for the injected memories block each turn — fewer, better
    /// memories beat many mediocre ones. 0 disables the cap.
    /// </summary>
    public int RecallBudgetTokens { get; init; } = 400;

    /// <summary>
    /// Cap on a single tool result's characters before it enters the context. JSON results
    /// are shrunk structurally (leaves clipped, structure intact); plain text is cut with a
    /// marker. 0 disables the cap.
    /// </summary>
    public int MaxToolResultChars { get; init; } = 16_000;
}
