using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CloudAgent.Core.Tools.Web;

/// <summary>One heading-delimited part of a page.</summary>
/// <param name="Number">1-based position, as shown to the agent.</param>
/// <param name="Level">Heading level (1 to 6); 0 for a page with no headings.</param>
/// <param name="Title">Heading text.</param>
/// <param name="Start">Character offset where the section starts.</param>
/// <param name="Length">Section length in characters.</param>
public sealed record WebPageSection(int Number, int Level, string Title, int Start, int Length);

/// <summary>A fetched page in readable form, kept so the agent can query or read it without fetching again.</summary>
public sealed record WebPage(
    string Id,
    string Url,
    string Title,
    DateTimeOffset FetchedAt,
    string Markdown,
    bool Truncated)
{
    private IReadOnlyList<WebPageSection>? _sections;

    /// <summary>The page's parts, derived from its markdown headings.</summary>
    public IReadOnlyList<WebPageSection> Sections => _sections ??= BuildSections(Markdown);

    /// <summary>Stable short id for a URL.</summary>
    public static string IdFor(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexStringLower(hash)[..12];
    }

    private static List<WebPageSection> BuildSections(string markdown)
    {
        var headings = new List<(int Level, string Title, int Start)>();
        var offset = 0;
        foreach (var line in markdown.Split('\n'))
        {
            var level = 0;
            while (level < line.Length && level < 6 && line[level] == '#')
                level++;
            if (level > 0 && level < line.Length && line[level] == ' ')
                headings.Add((level, line[(level + 1)..].Trim(), offset));
            offset += line.Length + 1;
        }

        if (headings.Count == 0)
            return [new WebPageSection(1, 0, "Whole page", 0, markdown.Length)];

        var sections = new List<WebPageSection>();
        if (headings[0].Start > 0)
            sections.Add(new WebPageSection(1, 0, "Introduction", 0, headings[0].Start));

        for (var i = 0; i < headings.Count; i++)
        {
            var (level, title, start) = headings[i];
            var end = markdown.Length;
            for (var j = i + 1; j < headings.Count; j++)
            {
                if (headings[j].Level <= level)
                {
                    end = headings[j].Start;
                    break;
                }
            }
            sections.Add(new WebPageSection(sections.Count + 1, level, title, start, end - start));
        }

        return sections;
    }
}

/// <summary>
/// The agent's cache of fetched pages: one JSON file per page under a directory in the workspace,
/// considered fresh for <see cref="Ttl"/>.
/// </summary>
public sealed class WebPageStore(string directory)
{
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(24);

    /// <summary>A fresh cached page for the URL, or null.</summary>
    public WebPage? FindFresh(string url)
    {
        var page = Find(WebPage.IdFor(url));
        return page is not null && DateTimeOffset.UtcNow - page.FetchedAt <= Ttl ? page : null;
    }

    /// <summary>A cached page by id regardless of age, or null.</summary>
    public WebPage? Find(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            return new WebPage(
                json["id"]!.GetValue<string>(),
                json["url"]!.GetValue<string>(),
                json["title"]!.GetValue<string>(),
                DateTimeOffset.Parse(json["fetchedAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
                json["markdown"]!.GetValue<string>(),
                json["truncated"]!.GetValue<bool>());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException or FormatException)
        {
            return null;
        }
    }

    public void Save(WebPage page)
    {
        Directory.CreateDirectory(directory);
        var json = new JsonObject
        {
            ["id"] = page.Id,
            ["url"] = page.Url,
            ["title"] = page.Title,
            ["fetchedAt"] = page.FetchedAt.ToString("O"),
            ["markdown"] = page.Markdown,
            ["truncated"] = page.Truncated,
        };
        File.WriteAllText(PathFor(page.Id), json.ToJsonString());
    }

    private string PathFor(string id) => Path.Combine(directory, $"{id}.json");
}
