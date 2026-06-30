using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Workspaces;

namespace CloudAgent.Core.Tools.Files;

/// <summary>Lists the entries of a folder in the workspace.</summary>
public sealed class ListDirectoryTool(IWorkspace workspace) : ITool
{
    public string Name => "list_directory";

    public string Description => "List files and subfolders of a workspace directory (defaults to the root).";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("path", "Workspace-relative folder path. Defaults to the root.", required: false),
    ];

    public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var relative = input.GetStringOrNull("path") ?? ".";
        var path = workspace.Resolve(relative);

        if (!Directory.Exists(path))
            return Task.FromResult(ToolResult.Fail($"Directory not found: {relative}"));

        var sb = new StringBuilder();
        foreach (var dir in Directory.EnumerateDirectories(path).OrderBy(p => p))
            sb.AppendLine($"[dir]  {Path.GetFileName(dir)}/");

        foreach (var file in Directory.EnumerateFiles(path).OrderBy(p => p))
        {
            var size = new FileInfo(file).Length;
            sb.AppendLine($"[file] {Path.GetFileName(file)} ({size} bytes)");
        }

        var listing = sb.Length == 0 ? "(empty)" : sb.ToString().TrimEnd();
        return Task.FromResult(ToolResult.Ok(listing));
    }
}
