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

    private static void MeasureEphemeral(string ephemeral, Action<string, int> add)
    {
        if (ephemeral.Length == 0)
            return;

        var starts = new List<int>();
        if (ephemeral.StartsWith("## ", StringComparison.Ordinal))
            starts.Add(0);
        for (var i = ephemeral.IndexOf("\n\n## ", StringComparison.Ordinal); i >= 0; i = ephemeral.IndexOf("\n\n## ", i + 2, StringComparison.Ordinal))
            starts.Add(i + 2);

        if (starts.Count == 0 || starts[0] > 0)
            add(PromptPart.Other, starts.Count == 0 ? ephemeral.Length : starts[0]);

        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : ephemeral.Length;
            var section = ephemeral.AsSpan(starts[i], end - starts[i]);
            var part = section.StartsWith(AttentionRendering.StateHeader, StringComparison.Ordinal) ? PromptPart.State
                : section.StartsWith(AttentionRendering.SummaryHeader, StringComparison.Ordinal) ? PromptPart.Summary
                : section.StartsWith(AttentionRendering.RecalledHeader, StringComparison.Ordinal) ? PromptPart.Recalled
                : section.StartsWith(AttentionRendering.StubsHeader, StringComparison.Ordinal) ? PromptPart.Stubs
                : PromptPart.Other;
            add(part, section.Length);
        }
    }
}
