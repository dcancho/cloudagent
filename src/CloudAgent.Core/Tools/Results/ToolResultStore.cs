using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CloudAgent.Core.Tools.Results;

/// <summary>
/// Whole tool results kept on disk under short ids, so the context can carry only the start of a large one
/// and the agent can read the rest on demand. A stored result never changes.
/// </summary>
public sealed partial class ToolResultStore(string directory)
{
    [GeneratedRegex("^[0-9a-f]{12}$")]
    private static partial Regex ValidId();

    /// <summary>Store <paramref name="content"/> and return its id.</summary>
    public string Save(string content)
    {
        Directory.CreateDirectory(directory);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        File.WriteAllText(PathOf(id), content);
        return id;
    }

    /// <summary>The characters from <paramref name="offset"/> on, at most <paramref name="length"/>, or null when there is no such result.</summary>
    public string? Read(string id, int offset, int length)
    {
        if (!ValidId().IsMatch(id) || !File.Exists(PathOf(id)))
            return null;

        var content = File.ReadAllText(PathOf(id));
        offset = Math.Clamp(offset, 0, content.Length);
        return content.Substring(offset, Math.Min(Math.Max(length, 0), content.Length - offset));
    }

    /// <summary>How many characters the stored result has, or null when there is no such result.</summary>
    public int? LengthOf(string id) =>
        ValidId().IsMatch(id) && File.Exists(PathOf(id)) ? File.ReadAllText(PathOf(id)).Length : null;

    /// <summary>Delete the results last written before <paramref name="cutoff"/>.</summary>
    public int Prune(DateTimeOffset cutoff)
    {
        if (!Directory.Exists(directory))
            return 0;

        var old = Directory.EnumerateFiles(directory, "*.txt").Where(f => File.GetLastWriteTimeUtc(f) < cutoff).ToList();
        old.ForEach(File.Delete);
        return old.Count;
    }

    private string PathOf(string id) => Path.Combine(directory, id + ".txt");
}
