using System;
using System.Collections.Generic;
using System.Linq;
using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Providers;

namespace CloudAgent.Core.Loop;

/// <summary>
/// Keeps a request's estimated input under a token cap by dropping what matters least, in order:
/// set-aside stubs, recalled memories from the least relevant up, the rolling summary, then the
/// bodies of the older tool results. The conversation itself is never cut.
/// </summary>
public static class InputCap
{
    private const int CharsPerToken = 4;
    private const int KeepRecentMessages = 4;
    private const int ToolResultKeepChars = 400;

    /// <summary>
    /// <paramref name="request"/> trimmed to <paramref name="maxInputTokens"/> (about 4 characters per token),
    /// with <paramref name="notice"/> appended to its ephemeral context after trimming. Nothing is trimmed when
    /// the request already fits or the cap is not positive.
    /// </summary>
    public static LlmRequest Fit(LlmRequest request, int maxInputTokens, string? notice = null)
    {
        var sections = PromptComposition.Sections(request.EphemeralContext ?? string.Empty);
        var messages = request.Messages.ToList();
        var trimmed = new List<string>();
        var limit = (long)maxInputTokens * CharsPerToken;

        LlmRequest Build()
        {
            var ephemeral = string.Concat(sections.Select(s => s.Text)).TrimEnd();
            if (!string.IsNullOrWhiteSpace(notice))
                ephemeral = ephemeral.Length == 0 ? notice : $"{ephemeral}\n\n{notice}";
            return new LlmRequest
            {
                System = request.System,
                EphemeralContext = ephemeral.Length == 0 ? null : ephemeral,
                Messages = messages,
                Model = request.Model,
                Purpose = request.Purpose,
                MaxTokens = request.MaxTokens,
                Temperature = request.Temperature,
                Tools = request.Tools,
                Trimmed = trimmed.Count == 0 ? null : string.Join(", ", trimmed),
            };
        }

        bool Over() => maxInputTokens > 0 && PromptComposition.Measure(Build()).Values.Sum(v => (long)v) > limit;

        if (Over())
        {
            if (sections.RemoveAll(s => s.Part == PromptPart.Stubs) > 0)
                trimmed.Add("stubs");
            if (Over())
                DropRecalled(sections, trimmed, Over);
            if (Over() && sections.RemoveAll(s => s.Part == PromptPart.Summary) > 0)
                trimmed.Add("summary");
            if (Over())
                ShrinkToolResults(messages, trimmed, Over);
            if (Over())
                trimmed.Add("still over the cap");
        }

        return Build();
    }

    private static void DropRecalled(List<(string Part, string Text)> sections, List<string> trimmed, Func<bool> over)
    {
        var at = sections.FindIndex(s => s.Part == PromptPart.Recalled);
        if (at < 0)
            return;

        var lines = sections[at].Text.TrimEnd().Split('\n');
        var firstItem = Array.FindIndex(lines, l => l.StartsWith("- ", StringComparison.Ordinal));
        if (firstItem < 0)
            return;

        var head = string.Join('\n', lines.Take(firstItem));
        var items = new List<string>();
        foreach (var line in lines.Skip(firstItem))
        {
            if (line.StartsWith("- ", StringComparison.Ordinal))
                items.Add(line);
            else
                items[^1] += "\n" + line;
        }

        var dropped = 0;
        while (items.Count > 0 && over())
        {
            items.RemoveAt(items.Count - 1);
            dropped++;
            if (items.Count == 0)
                sections.RemoveAt(at);
            else
                sections[at] = (PromptPart.Recalled, head + "\n" + string.Join('\n', items) + "\n\n");
        }

        if (dropped > 0)
            trimmed.Add($"{dropped} recalled memor{(dropped == 1 ? "y" : "ies")}");
    }

    private static void ShrinkToolResults(List<Message> messages, List<string> trimmed, Func<bool> over)
    {
        var candidates = Enumerable.Range(0, Math.Max(0, messages.Count - KeepRecentMessages))
            .Where(i => messages[i].Role == Role.Tool && messages[i].Content.Length > ToolResultKeepChars)
            .OrderByDescending(i => messages[i].Content.Length)
            .ToList();

        var count = 0;
        foreach (var index in candidates)
        {
            messages[index] = messages[index] with
            {
                Content = messages[index].Content[..ToolResultKeepChars] + "\n[older tool result cut to fit the input limit]",
            };
            count++;
            if (!over())
                break;
        }

        if (count > 0)
            trimmed.Add($"{count} tool result{(count == 1 ? "" : "s")}");
    }
}
