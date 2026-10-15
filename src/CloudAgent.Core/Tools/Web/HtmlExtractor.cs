using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace CloudAgent.Core.Tools.Web;

/// <summary>The readable part of an HTML page.</summary>
/// <param name="Title">The page title, if any.</param>
/// <param name="Markdown">The main content as lightweight markdown.</param>
/// <param name="NeedsScript">Whether the page appears to render its content with JavaScript.</param>
public sealed record ExtractedPage(string Title, string Markdown, bool NeedsScript);

/// <summary>
/// Turns HTML into compact markdown: keeps the main content (headings, paragraphs, lists, code,
/// tables) and drops navigation, scripts, forms, and other chrome. Raw HTML never reaches the model.
/// </summary>
public static partial class HtmlExtractor
{
    private const int MinReadableChars = 200;

    private static readonly string[] AlwaysRemoved =
    [
        "script", "style", "noscript", "template", "svg", "canvas", "iframe", "form", "button",
        "select", "input", "nav", "aside", "[hidden]", "[aria-hidden=true]",
    ];

    public static ExtractedPage Extract(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        var hadScripts = document.QuerySelectorAll("script").Length > 0;
        var title = Collapse(document.Title ?? string.Empty);

        IElement? root = document.QuerySelector("main")
            ?? document.QuerySelector("article")
            ?? document.QuerySelector("[role=main]")
            ?? document.Body;
        if (root is null)
            return new ExtractedPage(title, string.Empty, hadScripts);

        foreach (var selector in AlwaysRemoved)
            foreach (var element in root.QuerySelectorAll(selector).ToArray())
                element.Remove();

        if (root == document.Body)
            foreach (var element in root.QuerySelectorAll("header, footer").ToArray())
                element.Remove();

        var sb = new StringBuilder();
        Render(root, sb);
        var markdown = Tidy(sb.ToString());

        var readable = markdown.Count(c => !char.IsWhiteSpace(c) && c != '#' && c != '-');
        return new ExtractedPage(title, markdown, hadScripts && readable < MinReadableChars);
    }

    /// <summary>Plain text passes through with only whitespace tidied.</summary>
    public static ExtractedPage FromPlainText(string text) => new(string.Empty, Tidy(text), false);

    private static void Render(INode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText text)
            {
                sb.Append(CollapseInline(text.Data));
                continue;
            }

            if (child is not IElement element)
                continue;

            switch (element.LocalName)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    var level = element.LocalName[1] - '0';
                    var heading = Collapse(element.TextContent);
                    if (heading.Length > 0)
                        sb.Append("\n\n").Append('#', level).Append(' ').Append(heading).Append("\n\n");
                    break;
                case "pre":
                    sb.Append("\n\n```\n").Append(element.TextContent.Trim('\n')).Append("\n```\n\n");
                    break;
                case "code":
                    sb.Append('`').Append(Collapse(element.TextContent)).Append('`');
                    break;
                case "br":
                    sb.Append('\n');
                    break;
                case "li":
                    sb.Append("\n- ");
                    Render(element, sb);
                    break;
                case "tr":
                    var cells = element.Children
                        .Where(c => c.LocalName is "td" or "th")
                        .Select(c => Collapse(c.TextContent));
                    sb.Append("\n| ").Append(string.Join(" | ", cells)).Append(" |");
                    break;
                case "img":
                    break;
                case "p" or "div" or "section" or "article" or "main" or "header" or "footer"
                    or "ul" or "ol" or "table" or "blockquote" or "figure" or "dl" or "dd" or "dt":
                    sb.Append("\n\n");
                    Render(element, sb);
                    sb.Append("\n\n");
                    break;
                default:
                    Render(element, sb);
                    break;
            }
        }
    }

    private static string Tidy(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd());
        var joined = string.Join('\n', lines);
        return ExcessBlankLines().Replace(joined, "\n\n").Trim();
    }

    private static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

    private static string CollapseInline(string text) => Whitespace().Replace(text, " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\n[ \t]*\n(?:[ \t]*\n)+")]
    private static partial Regex ExcessBlankLines();
}
