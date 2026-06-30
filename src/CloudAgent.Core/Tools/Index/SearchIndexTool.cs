using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Indexing;

namespace CloudAgent.Core.Tools.Index;

/// <summary>Semantic search over the workspace index: embeds the query and returns the closest chunks.</summary>
public sealed class SearchIndexTool(IEmbeddingProvider embeddings, VectorIndex index) : ITool
{
    private const int DefaultTopK = 5;
    private const int SnippetChars = 240;

    public string Name => "search_index";

    public string Description =>
        "Search the indexed workspace by meaning. Returns the most relevant chunks with their source file and a snippet.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("query", "What to search for, in natural language."),
        ToolParameter.Integer("top_k", "How many results to return (default 5).", required: false),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        if (index.Count == 0)
            return ToolResult.Fail("The index is empty. Run index_workspace first.");

        var query = input.GetString("query");
        var topK = input.GetInt("top_k", DefaultTopK);

        var queryVector = await embeddings.EmbedAsync(query, cancellationToken).ConfigureAwait(false);
        var hits = index.Search(queryVector, topK);

        var sb = new StringBuilder();
        var rank = 1;
        foreach (var hit in hits)
        {
            var snippet = hit.Chunk.Text.Length > SnippetChars
                ? hit.Chunk.Text[..SnippetChars] + "…"
                : hit.Chunk.Text;
            snippet = snippet.ReplaceLineEndings(" ");

            sb.AppendLine($"{rank++}. [{hit.Score:F3}] {hit.Chunk.Source} (chunk {hit.Chunk.Ordinal})");
            sb.AppendLine($"   {snippet}");
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }
}
