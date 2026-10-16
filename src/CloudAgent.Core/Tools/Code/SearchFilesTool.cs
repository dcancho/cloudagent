using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Workspaces;

namespace CloudAgent.Core.Tools.Code;

/// <summary>
/// Searches file contents with a regular expression and returns matching lines with their paths
/// and line numbers. Exact, unlike the semantic index: it finds every use of a name. Build output,
/// dependencies, and version control folders are skipped, and so are binary and very large files.
/// </summary>
public sealed class SearchFilesTool(IWorkspace workspace) : ITool
{
    private const int MaxFileBytes = 1_000_000;
    private const int MaxLineChars = 200;
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".agent", "bin", "obj", "node_modules", "dist", "build", "packages",
    };

    public string Name => "search_files";

    public string Description =>
        "Search file contents with a regular expression and get matching lines as path:line: text. " +
        "Use it to find every use of a name, a string, or a pattern.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("pattern", "A .NET regular expression, for example: class \\w+Tool or TODO."),
        ToolParameter.String("path", "Workspace-relative directory to search; defaults to the root.", required: false),
        ToolParameter.String("file_pattern", "Only search file names matching this wildcard, for example *.cs.", required: false),
        ToolParameter.Boolean("ignore_case", "Match regardless of case.", required: false),
        ToolParameter.Integer("max_results", "Stop after this many matching lines; default 100.", required: false),
    ];

    public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        Regex regex;
        try
        {
            var options = RegexOptions.CultureInvariant | (input.GetBool("ignore_case") ? RegexOptions.IgnoreCase : 0);
            regex = new Regex(input.GetString("pattern"), options, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolResult.Fail($"Invalid pattern: {ex.Message}"));
        }

        var root = workspace.Resolve(input.GetStringOrNull("path") ?? "");
        if (!Directory.Exists(root))
            return Task.FromResult(ToolResult.Fail($"Directory not found: {input.GetStringOrNull("path")}"));

        var filePattern = input.GetStringOrNull("file_pattern");
        var maxResults = Math.Clamp(input.GetInt("max_results", 100), 1, 1000);

        var results = new StringBuilder();
        var found = 0;
        var files = 0;
        foreach (var file in Files(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filePattern is not null && !FileSystemName.MatchesSimpleExpression(filePattern, Path.GetFileName(file)))
                continue;

            var lines = ReadText(file);
            if (lines is null)
                continue;

            var matchedFile = false;
            for (var i = 0; i < lines.Length && found < maxResults; i++)
            {
                bool match;
                try
                {
                    match = regex.IsMatch(lines[i]);
                }
                catch (RegexMatchTimeoutException)
                {
                    break;
                }
                if (!match)
                    continue;

                var text = lines[i].Trim();
                if (text.Length > MaxLineChars)
                    text = text[..MaxLineChars] + "...";
                results.Append(workspace.ToRelative(file)).Append(':').Append(i + 1).Append(": ").AppendLine(text);
                found++;
                matchedFile = true;
            }
            if (matchedFile)
                files++;
            if (found >= maxResults)
            {
                results.AppendLine($"... stopped at {maxResults} results; narrow the pattern or the path.");
                break;
            }
        }

        return Task.FromResult(ToolResult.Ok(found == 0
            ? "No matches."
            : $"{found} match(es) in {files} file(s):\n{results.ToString().TrimEnd()}"));
    }

    private static IEnumerable<string> Files(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries.Reverse())
            {
                if (Directory.Exists(entry))
                {
                    if (!SkippedDirectories.Contains(Path.GetFileName(entry)))
                        pending.Push(entry);
                }
            }
            foreach (var entry in entries)
            {
                if (File.Exists(entry))
                    yield return entry;
            }
        }
    }

    private static string[]? ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
                return null;

            var bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
                return null;

            return Encoding.UTF8.GetString(bytes).Split('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
