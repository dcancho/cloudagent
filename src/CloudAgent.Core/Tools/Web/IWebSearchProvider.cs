using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Tools.Web;

/// <summary>A single web result.</summary>
/// <param name="Title">Page title.</param>
/// <param name="Url">Result URL.</param>
/// <param name="Snippet">Short extract / summary.</param>
public sealed record WebSearchHit(string Title, string Url, string Snippet);

/// <summary>
/// The seam to a web-search backend (Tavily, Bing, Brave, ...). Same pattern as the LLM
/// provider: the tool depends on this interface, not on any particular search service.
/// </summary>
public interface IWebSearchProvider
{
    string Name { get; }

    Task<IReadOnlyList<WebSearchHit>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default);
}

/// <summary>A search provider that returns nothing — the unconfigured default.</summary>
public sealed class NullWebSearchProvider : IWebSearchProvider
{
    public string Name => "none";

    public Task<IReadOnlyList<WebSearchHit>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WebSearchHit>>([]);
}
