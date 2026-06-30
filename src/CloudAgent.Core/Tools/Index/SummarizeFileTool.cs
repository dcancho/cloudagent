using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Indexing;
using CloudAgent.Core.Workspaces;

namespace CloudAgent.Core.Tools.Index;

/// <summary>Reads a workspace file and returns an LLM-generated summary.</summary>
public sealed class SummarizeFileTool(IWorkspace workspace, Summarizer summarizer) : ITool
{
    public string Name => "summarize_file";

    public string Description => "Read a text file in the workspace and produce a concise summary of it.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("path", "Workspace-relative path to the file to summarize."),
        ToolParameter.String("instruction", "Optional focus for the summary.", required: false),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var relative = input.GetString("path");
        var path = workspace.Resolve(relative);
        if (!File.Exists(path))
            return ToolResult.Fail($"File not found: {relative}");

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            return ToolResult.Fail($"File is empty: {relative}");

        var summary = await summarizer
            .SummarizeAsync(text, input.GetStringOrNull("instruction"), cancellationToken)
            .ConfigureAwait(false);

        return ToolResult.Ok(summary);
    }
}
