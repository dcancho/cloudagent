using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Tools.Web;

/// <summary>
/// Web search via the Tavily API (https://tavily.com) — a search service built for agents
/// that returns clean title/url/content results from a single POST. No SDK; just HTTP.
/// </summary>
public sealed class TavilyWebSearchProvider : IWebSearchProvider
{
    private const string Endpoint = "https://api.tavily.com/search";

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public TavilyWebSearchProvider(string apiKey, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("A Tavily API key is required.", nameof(apiKey));

        _apiKey = apiKey;
        _http = http ?? new HttpClient();
    }

    public string Name => "tavily";

    public async Task<IReadOnlyList<WebSearchHit>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            api_key = _apiKey,
            query,
            max_results = maxResults,
            search_depth = "basic",
        };

        using var response = await _http
            .PostAsJsonAsync(Endpoint, payload, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Tavily returned {(int)response.StatusCode}: {body}");

        var hits = new List<WebSearchHit>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("results", out var results) &&
            results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                hits.Add(new WebSearchHit(
                    item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                    item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : ""));
            }
        }

        return hits;
    }
}
