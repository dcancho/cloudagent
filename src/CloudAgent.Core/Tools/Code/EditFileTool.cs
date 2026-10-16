using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Workspaces;

namespace CloudAgent.Core.Tools.Code;

/// <summary>
/// Replaces exact text in a file. The text is the anchor, never a line number, because line
/// numbers move between turns. An anchor that is missing or appears more than once is an error,
/// so an edit never lands somewhere the agent did not mean. The file keeps its line endings and
/// byte order mark.
/// </summary>
public sealed class EditFileTool(IWorkspace workspace) : ITool
{
    public string Name => "edit_file";

    public string Description =>
        "Replace exact text in an existing file. old_text must match the file exactly, including indentation, " +
        "and appear exactly once unless replace_all is true; include surrounding lines to make it unique. " +
        "Read the file first. To create a file, use write_file.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("path", "Workspace-relative path to the file."),
        ToolParameter.String("old_text", "The exact text to replace."),
        ToolParameter.String("new_text", "The text to put in its place; empty to delete it."),
        ToolParameter.Boolean("replace_all", "Replace every occurrence instead of exactly one.", required: false),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var relative = input.GetString("path");
        var path = workspace.Resolve(relative);
        if (!File.Exists(path))
            return ToolResult.Fail($"File not found: {relative}. Use write_file to create it.");

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var hasBom = bytes is [0xEF, 0xBB, 0xBF, ..];
        var content = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));

        var oldText = input.GetString("old_text");
        var newText = input.GetStringOrNull("new_text") ?? string.Empty;
        if (content.Contains("\r\n", StringComparison.Ordinal))
        {
            oldText = ToCrLf(oldText);
            newText = ToCrLf(newText);
        }

        var count = Count(content, oldText);
        if (count == 0)
            return ToolResult.Fail($"old_text was not found in {relative}. Read the file again and copy the text exactly.");

        var replaceAll = input.GetBool("replace_all");
        if (count > 1 && !replaceAll)
            return ToolResult.Fail($"old_text appears {count} times in {relative}. Include more surrounding text to make it unique, or set replace_all.");

        var first = content.IndexOf(oldText, StringComparison.Ordinal);
        var updated = replaceAll
            ? content.Replace(oldText, newText, StringComparison.Ordinal)
            : string.Concat(content.AsSpan(0, first), newText, content.AsSpan(first + oldText.Length));

        await File.WriteAllTextAsync(path, updated, new UTF8Encoding(hasBom), cancellationToken).ConfigureAwait(false);

        var line = LineOf(updated, first);
        var lines = Math.Max(1, Count(newText, "\n") + 1);
        return ToolResult.Ok(replaceAll && count > 1
            ? $"Replaced {count} occurrences in {relative}; the first is at line {line}."
            : $"Edited {relative}, lines {line}-{line + lines - 1}.");
    }

    private static string ToCrLf(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static int LineOf(string text, int index) => Count(text[..index], "\n") + 1;
}
