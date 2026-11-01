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

    /// <summary>
    /// Most input tokens, estimated at about 4 characters each, one model call may carry. Past it the
    /// least useful context is dropped before the call (see <see cref="InputCap"/>). 0 means no cap.
    /// </summary>
    public int MaxInputTokens { get; init; } = 32_000;

    /// <summary>How many recent messages to keep verbatim when compacting.</summary>
    public int KeepRecentMessages { get; init; } = 12;

    // Recall knobs (RecallTopK/RecallThreshold/RecallBudgetTokens) now live in
    // CloudAgent.Core.Attention.AttentionOptions — they belong to the attention module.

    /// <summary>
    /// Cap on a single tool result's characters before it enters the context. JSON results
    /// are shrunk structurally (leaves clipped, structure intact); plain text is cut with a
    /// marker. 0 disables the cap.
    /// </summary>
    public int MaxToolResultChars { get; init; } = 16_000;

    /// <summary>
    /// Rounds of tool calls one turn may take, whatever they cost. When they run out, the agent answers with what it
    /// has instead of calling more tools.
    /// </summary>
    public int MaxToolSteps { get; init; } = 16;

    /// <summary>
    /// Points the tool calls of one turn may spend, each tool costing what <see cref="Tools.ToolWeights"/> says.
    /// When they run out, the agent answers with what it has. 0 means no limit beyond <see cref="MaxToolSteps"/>.
    /// </summary>
    public int MaxToolPoints { get; init; } = 30;

    /// <summary>
    /// Characters of a tool result that go into the context. A longer result is kept whole in the workspace
    /// and the context gets its start and an id the agent can read the rest with (see <see cref="Tools.Results.ReadResultTool"/>).
    /// 0 keeps every result inline up to <see cref="MaxToolResultChars"/>.
    /// </summary>
    public int MaxInlineResultChars { get; init; } = 3000;

    /// <summary>
    /// The token budget this agent spends from, if any. Once it is spent, a turn in progress ends in
    /// a reply without more tools, and later messages are answered without calling the model.
    /// </summary>
    public Providers.TokenBudget? Budget { get; init; }
}
