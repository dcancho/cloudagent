using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Workspaces;

namespace CloudAgent.Core.Tools.Files;

/// <summary>Writes (or appends) a UTF-8 text file in the workspace, creating parent folders.</summary>
public sealed class WriteFileTool(IWorkspace workspace) : ITool
{
    public string Name => "write_file";

    public string Description => "Create or overwrite a text file in the workspace. Set append=true to add to it.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("path", "Workspace-relative path to the file to write."),
        ToolParameter.String("content", "Text content to write."),
        ToolParameter.Boolean("append", "Append instead of overwrite.", required: false),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var relative = input.GetString("path");
        var path = workspace.Resolve(relative);
        var content = input.GetStringOrNull("content") ?? string.Empty;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (input.GetBool("append"))
            await File.AppendAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        else
            await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);

        return ToolResult.Ok($"Wrote {content.Length} chars to {relative}.");
    }
}
