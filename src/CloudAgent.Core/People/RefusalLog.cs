using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CloudAgent.Core.People;

/// <summary>An account the host turned away, with how often and when it last did.</summary>
/// <param name="Account"><c>surface:senderId</c>, as the person would be linked.</param>
/// <param name="Name">The name the surface showed for them, when it gave one.</param>
/// <param name="Count">How many messages were refused.</param>
/// <param name="LastDay">The day of the last host log the refusal appears in.</param>
public sealed record Refusal(string Account, string? Name, int Count, DateOnly LastDay);

/// <summary>Reads the refusals the host writes to its log, so an id can be found without searching the journal.</summary>
public static partial class RefusalLog
{
    /// <summary>
    /// Group the refusal lines by account, most recent first. <paramref name="lines"/> are the lines of
    /// each host log with the day that log is for.
    /// </summary>
    public static IReadOnlyList<Refusal> Parse(IEnumerable<(DateOnly Day, string Line)> lines) =>
        lines
            .Select(l => (l.Day, Match: RefusedLine().Match(l.Line)))
            .Where(l => l.Match.Success)
            .GroupBy(l => l.Match.Groups["account"].Value)
            .Select(g => new Refusal(
                g.Key,
                g.Select(l => l.Match.Groups["name"].Value).LastOrDefault(n => n.Length > 0),
                g.Count(),
                g.Max(l => l.Day)))
            .OrderByDescending(r => r.LastDay)
            .ThenByDescending(r => r.Count)
            .ToList();

    [GeneratedRegex(@"refused (?<account>[a-z][a-z0-9_-]*:\S+)(?: \((?<name>.*)\))?\s*$")]
    private static partial Regex RefusedLine();
}
