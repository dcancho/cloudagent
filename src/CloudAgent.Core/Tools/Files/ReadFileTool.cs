using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Workspaces;

namespace CloudAgent.Core.Tools.Files;

/// <summary>
/// Reads a UTF-8 text file from the workspace, whole or as a range of lines. A long file is cut
/// at <see cref="MaxLines"/> lines with a note saying how to read the rest.
/// </summary>
public sealed class ReadFileTool(IWorkspace workspace) : ITool
{
    private const int MaxLines = 2000;

    public string Name => "read_file";

    public string Description =>
        $"Read a text file in the workspace. Files longer than {MaxLines} lines are cut; use start_line and line_count to read further.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("path", "Workspace-relative path to the file to read."),
        ToolParameter.Integer("start_line", "First line to read, starting at 1.", required: false),
        ToolParameter.Integer("line_count", $"How many lines to read; at most {MaxLines}.", required: false),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var path = workspace.Resolve(input.GetString("path"));
        if (!File.Exists(path))
            return ToolResult.Fail($"File not found: {input.GetString("path")}");

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var ranged = input.Has("start_line") || input.Has("line_count");
        var lines = content.Split('\n');
        if (!ranged && lines.Length <= MaxLines)
            return ToolResult.Ok(content);

        var start = Math.Clamp(input.GetInt("start_line", 1), 1, Math.Max(1, lines.Length));
        var count = Math.Clamp(input.GetInt("line_count", MaxLines), 1, MaxLines);
        var end = Math.Min(lines.Length, start + count - 1);
        var header = $"Lines {start}-{end} of {lines.Length}.";
        if (end < lines.Length)
            header += $" Continue with start_line {end + 1}.";
        return ToolResult.Ok($"{header}\n{string.Join('\n', lines[(start - 1)..end])}");
    }
}
