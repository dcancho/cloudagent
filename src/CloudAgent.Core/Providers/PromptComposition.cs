using System;
using System.Collections.Generic;
using System.Linq;
using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Attention;

namespace CloudAgent.Core.Providers;

/// <summary>The parts a prompt is made of, as the usage ledger names them.</summary>
public static class PromptPart
{
    public const string Persona = "persona";
    public const string Standing = "standing memories";
    public const string State = "operational state";
    public const string Summary = "rolling summary";
    public const string Recalled = "recalled memories";
    public const string Stubs = "set-aside stubs";
    public const string Guidance = "turn guidance";
    public const string Tools = "tool definitions";
    public const string User = "user messages";
    public const string Assistant = "assistant messages";
    public const string ToolResults = "tool results";
    public const string Other = "other";
}

/// <summary>Measures how much of a request each part of the prompt takes, in characters.</summary>
public static class PromptComposition
{
    /// <summary>Characters per part; parts that are empty are left out.</summary>
    public static IReadOnlyDictionary<string, int> Measure(LlmRequest request)
    {
        var parts = new Dictionary<string, int>();

        void Add(string part, int chars)
        {
            if (chars > 0)
                parts[part] = parts.GetValueOrDefault(part) + chars;
        }

        var system = request.System ?? string.Empty;
        var standing = system.IndexOf(AttentionRendering.StandingHeader, StringComparison.Ordinal);
        Add(PromptPart.Persona, standing < 0 ? system.Length : standing);
        Add(PromptPart.Standing, standing < 0 ? 0 : system.Length - standing);

        MeasureEphemeral(request.EphemeralContext ?? string.Empty, Add);

        Add(PromptPart.Tools, request.Tools.Sum(t => t.Name.Length + t.Description.Length + t.Schema.ToJsonString().Length));

        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case Role.User:
                    Add(PromptPart.User, message.Content.Length);
                    break;
                case Role.Assistant:
                    Add(PromptPart.Assistant, message.Content.Length + message.ToolCalls.Sum(c => c.Name.Length + c.Arguments.Length));
                    break;
                case Role.Tool:
                    Add(PromptPart.ToolResults, message.Content.Length);
                    break;
                default:
                    Add(PromptPart.Persona, message.Content.Length);
                    break;
            }
        }
        return parts;
    }

    private static readonly (string Header, string Part)[] SectionHeaders =
    [
        (AttentionRendering.StateHeader, PromptPart.State),
        (AttentionRendering.SummaryHeader, PromptPart.Summary),
        (AttentionRendering.RecalledHeader, PromptPart.Recalled),
        (AttentionRendering.StubsHeader, PromptPart.Stubs),
        (AttentionRendering.GuidanceHeader, PromptPart.Guidance),
    ];

    /// <summary>
    /// The ephemeral context split at the headers the attention renderer and the loop write, each section
    /// with the part it counts as and its text up to the next section. A heading inside a memory or the summary
    /// is not a boundary. Text before the first header is returned as <see cref="PromptPart.Other"/>.
    /// </summary>
    public static List<(string Part, string Text)> Sections(string ephemeral)
    {
        var sections = new List<(string, string)>();
        if (ephemeral.Length == 0)
            return sections;

        var starts = new SortedSet<int>();
        foreach (var (header, _) in SectionHeaders)
        {
            if (ephemeral.StartsWith(header, StringComparison.Ordinal))
                starts.Add(0);
            for (var i = ephemeral.IndexOf("\n\n" + header, StringComparison.Ordinal); i >= 0; i = ephemeral.IndexOf("\n\n" + header, i + 2, StringComparison.Ordinal))
                starts.Add(i + 2);
        }

        var ordered = starts.ToList();
        if (ordered.Count == 0 || ordered[0] > 0)
            sections.Add((PromptPart.Other, ordered.Count == 0 ? ephemeral : ephemeral[..ordered[0]]));

        for (var i = 0; i < ordered.Count; i++)
        {
            var end = i + 1 < ordered.Count ? ordered[i + 1] : ephemeral.Length;
            var text = ephemeral[ordered[i]..end];
            var part = SectionHeaders.FirstOrDefault(h => text.StartsWith(h.Header, StringComparison.Ordinal)).Part ?? PromptPart.Other;
            sections.Add((part, text));
        }
        return sections;
    }

    private static void MeasureEphemeral(string ephemeral, Action<string, int> add)
    {
        foreach (var (part, text) in Sections(ephemeral))
            add(part, text.Length);
    }
}
