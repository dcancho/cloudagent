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
}
