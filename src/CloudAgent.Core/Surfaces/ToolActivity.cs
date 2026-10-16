using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Tools;

namespace CloudAgent.Core.Surfaces;

/// <summary>
/// One tool call of the agent a conversation talks to, summarized for the people in it. Subagents'
/// calls are not reported: delegation is internal to the agent.
/// </summary>
/// <param name="Tool">The tool's name.</param>
/// <param name="Success">Whether the call succeeded.</param>
/// <param name="Summary">One line on what was done and what came of it.</param>
/// <param name="Path">The file it acted on, when there is one.</param>
/// <param name="Before">For an edit, the text that was replaced.</param>
/// <param name="After">For an edit, the text put in its place.</param>
public sealed record ToolActivity(
    string Tool, bool Success, string Summary, string? Path = null, string? Before = null, string? After = null)
{
    private const int MaxSummaryChars = 160;

    public static ToolActivity From(ToolCall call, ToolResult result)
    {
        JsonObject? args;
        try
        {
            args = JsonNode.Parse(call.Arguments) as JsonObject;
        }
        catch (JsonException)
        {
            args = null;
        }

        string? Arg(string name) =>
            args?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        var outcome = result.Success ? FirstLine(result.Content) : FirstLine(result.Error ?? "failed");
        var path = Arg("path");
        var summary = call.Name switch
        {
            "run_command" => $"{Arg("command")}: {outcome}",
            "search_files" => $"{Arg("pattern")}: {outcome}",
            _ when path is not null => result.Success ? path : $"{path}: {outcome}",
            _ => result.Success ? "" : outcome,
        };

        return call.Name == "edit_file" && result.Success
            ? new ToolActivity(call.Name, true, Cut(summary), path, Arg("old_text"), Arg("new_text"))
            : new ToolActivity(call.Name, result.Success, Cut(summary), path);
    }

    private static string FirstLine(string text)
    {
        var line = text.AsSpan().Trim();
        var end = line.IndexOfAny('\r', '\n');
        return (end < 0 ? line : line[..end]).ToString();
    }

    private static string Cut(string text) =>
        text.Length <= MaxSummaryChars ? text : text[..MaxSummaryChars] + "...";
}

/// <summary>A surface that can show what the agent is doing while it works on a turn.</summary>
public interface IActivityIndicator : ISurface
{
    Task SendActivityAsync(string conversationId, ToolActivity activity, CancellationToken cancellationToken = default);
}
