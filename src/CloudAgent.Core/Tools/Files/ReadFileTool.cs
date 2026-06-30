using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Workspaces;

namespace CloudAgent.Core.Tools.Files;

/// <summary>Reads a UTF-8 text file from the workspace.</summary>
public sealed class ReadFileTool(IWorkspace workspace) : ITool
{
    public string Name => "read_file";

    public string Description => "Read the contents of a text file in the workspace.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("path", "Workspace-relative path to the file to read."),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var path = workspace.Resolve(input.GetString("path"));
        if (!File.Exists(path))
            return ToolResult.Fail($"File not found: {input.GetString("path")}");

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(content);
    }
}
