using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Indexing;

namespace CloudAgent.Core.Tools.Index;

/// <summary>(Re)builds the semantic index over the workspace's text files and persists it.</summary>
public sealed class IndexWorkspaceTool(WorkspaceIndexer indexer, VectorIndex index, string indexPath) : ITool
{
    public string Name => "index_workspace";

    public string Description =>
        "Scan the workspace and (re)build the semantic index over its text files, so it can be searched by meaning.";

    public IReadOnlyList<ToolParameter> Parameters => [];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var stats = await indexer.IndexAsync(cancellationToken).ConfigureAwait(false);
        index.Save(indexPath);

        return ToolResult.Ok(
            $"Indexed {stats.FilesIndexed} file(s), {stats.ChunksIndexed} chunk(s); " +
            $"{stats.FilesSkipped} skipped. Index now holds {index.Count} chunk(s).");
    }
}
