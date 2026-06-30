namespace CloudAgent.Core.Tools;

/// <summary>
/// The outcome of a tool invocation. <see cref="Content"/> is the text fed back to the
/// agent; <see cref="Error"/> carries a failure reason without throwing across the loop.
/// </summary>
/// <param name="Success">Whether the tool ran successfully.</param>
/// <param name="Content">Result text the agent observes.</param>
/// <param name="Error">Failure reason, when <paramref name="Success"/> is false.</param>
public sealed record ToolResult(bool Success, string Content, string? Error = null)
{
    public static ToolResult Ok(string content) => new(true, content);

    public static ToolResult Fail(string error) => new(false, string.Empty, error);
}
