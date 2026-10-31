using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Tools.Web;

/// <summary>Lets the agent search the web through whatever <see cref="IWebSearchProvider"/> is wired in.</summary>
public sealed class WebSearchTool(IWebSearchProvider provider) : ITool
{
    private const int DefaultMaxResults = 5;
    private const int SnippetChars = 320;

    public string Name => "web_search";

    public string Description => "Search the web and return a list of result titles, URLs, and snippets.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("query", "What to search for."),
        ToolParameter.Integer("max_results", "Maximum number of results (default 5).", required: false),
    ];

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var query = input.GetString("query");
        var max = input.GetInt("max_results", DefaultMaxResults);

        var hits = await provider.SearchAsync(query, max, cancellationToken).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return provider is NullWebSearchProvider
                ? ToolResult.Fail("Web search is not configured (no search provider set).")
                : ToolResult.Ok($"No results for \"{query}\".");
        }

        var sb = new StringBuilder();
        var i = 1;
        foreach (var hit in hits)
        {
            sb.AppendLine($"{i++}. {hit.Title}");
            sb.AppendLine($"   {hit.Url}");
            if (!string.IsNullOrWhiteSpace(hit.Snippet))
                sb.AppendLine($"   {Clip(hit.Snippet)}");
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static string Clip(string snippet)
    {
        var flat = string.Join(' ', snippet.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= SnippetChars ? flat : flat[..SnippetChars] + "...";
    }
}
