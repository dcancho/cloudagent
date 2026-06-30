using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Workspaces;

namespace CloudAgent.Core.Tools.Files;

/// <summary>Creates a folder (and any missing parents) in the workspace.</summary>
public sealed class CreateDirectoryTool(IWorkspace workspace) : ITool
{
    public string Name => "create_directory";

    public string Description => "Create a folder in the workspace, including any missing parent folders.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("path", "Workspace-relative folder path to create."),
    ];

    public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var relative = input.GetString("path");
        var path = workspace.Resolve(relative);
        Directory.CreateDirectory(path);
        return Task.FromResult(ToolResult.Ok($"Created directory {relative}."));
    }
}
