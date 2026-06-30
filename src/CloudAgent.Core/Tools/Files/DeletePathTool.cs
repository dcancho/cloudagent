using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Workspaces;

namespace CloudAgent.Core.Tools.Files;

/// <summary>
/// Deletes a file or folder in the workspace. Deleting a non-empty folder requires
/// <c>recursive=true</c> — a guard against wiping a populated directory by accident.
/// </summary>
public sealed class DeletePathTool(IWorkspace workspace) : ITool
{
    public string Name => "delete_path";

    public string Description => "Delete a file or folder in the workspace. Use recursive=true for a non-empty folder.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("path", "Workspace-relative path to delete."),
        ToolParameter.Boolean("recursive", "Delete folder contents recursively.", required: false),
    ];

    public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var relative = input.GetString("path");
        var path = workspace.Resolve(relative);

        if (File.Exists(path))
        {
            File.Delete(path);
            return Task.FromResult(ToolResult.Ok($"Deleted file {relative}."));
        }

        if (Directory.Exists(path))
        {
            var recursive = input.GetBool("recursive");
            if (!recursive && Directory.EnumerateFileSystemEntries(path).Any())
                return Task.FromResult(ToolResult.Fail(
                    $"Directory '{relative}' is not empty. Pass recursive=true to delete it."));

            Directory.Delete(path, recursive);
            return Task.FromResult(ToolResult.Ok($"Deleted directory {relative}."));
        }

        return Task.FromResult(ToolResult.Fail($"Path not found: {relative}"));
    }
}
