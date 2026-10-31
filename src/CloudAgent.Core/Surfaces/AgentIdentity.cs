using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CloudAgent.Core.Surfaces;

/// <summary>
/// What the agent is called: a name and any aliases people use for it. A group message that says
/// one of them, as a whole word and regardless of case or accents, may be speaking to the agent.
/// </summary>
public sealed class AgentIdentity
{
    private readonly Regex _pattern;

    public AgentIdentity(string name, IEnumerable<string>? aliases = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("The agent needs a name.", nameof(name));

        Name = name.Trim();
        Aliases = (aliases ?? [])
            .Select(a => a.Trim())
            .Where(a => a.Length > 0 && !string.Equals(a, Name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var alternatives = new[] { Name }.Concat(Aliases).Select(n => Regex.Escape(Fold(n)));
        _pattern = new Regex(
            $@"(?<![\p{{L}}\p{{N}}_])(?:{string.Join('|', alternatives)})(?![\p{{L}}\p{{N}}_])",
            RegexOptions.CultureInvariant);
    }

    public string Name { get; }

    public IReadOnlyList<string> Aliases { get; }

    /// <summary>Whether the text says the agent's name or one of its aliases.</summary>
    public bool IsNamedIn(string text) => !string.IsNullOrEmpty(text) && _pattern.IsMatch(Fold(text));

    /// <summary>The name and aliases as one phrase, for prompts.</summary>
    public string Describe() => Aliases.Count == 0 ? Name : $"{Name} (also called {string.Join(", ", Aliases)})";

    /// <summary>Lowercase without accents, so "Atlás" and "atlas" read the same.</summary>
    private static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                folded.Append(c);
        }
        return folded.ToString().Normalize(NormalizationForm.FormC);
    }
}
