using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Providers;

namespace CloudAgent.Core.Loop;

/// <summary>A request after the input cap, and how many of its recalled memories it still carries.</summary>
/// <param name="Request">The request to send.</param>
/// <param name="RecalledKept">How many of the recalled memories it was given are still in it.</param>
public sealed record InputFit(LlmRequest Request, int RecalledKept);

/// <summary>
/// Keeps a request's estimated input under a token cap by dropping what matters least, in order:
/// set-aside stubs, recalled memories from the least relevant up, the rolling summary, then the
/// bodies of the older tool results. The conversation, the turn guidance and the notice are never cut.
/// </summary>
public static partial class InputCap
{
    private const int CharsPerToken = 4;
    private const int KeepRecentMessages = 4;
    private const int ToolResultKeepChars = 400;
    private const string ToolResultCut = "\n[older tool result cut to fit the input limit]";

    [GeneratedRegex(@"^- \([^)/\r\n]*/[^)\r\n]*\) ")]
    private static partial Regex MemoryItem();

    /// <summary>
    /// <paramref name="request"/> trimmed to <paramref name="maxInputTokens"/> (about 4 characters per token),
    /// with <paramref name="notice"/> appended to its ephemeral context after trimming. Nothing is trimmed when
    /// the request already fits or the cap is not positive.
    /// </summary>
    /// <param name="recalledMemories">How many memories its recalled section holds. If the section does not parse
    /// into that many, it is dropped whole or not at all.</param>
    public static InputFit Fit(LlmRequest request, int maxInputTokens, string? notice = null, int recalledMemories = 0)
    {
        var sections = PromptComposition.Sections(request.EphemeralContext ?? string.Empty);
        var messages = request.Messages.ToList();
        var trimmed = new List<string>();
        var limit = (long)maxInputTokens * CharsPerToken;
        var kept = recalledMemories;

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
                kept = DropRecalled(sections, trimmed, Over, recalledMemories);
            if (Over() && sections.RemoveAll(s => s.Part == PromptPart.Summary) > 0)
                trimmed.Add("summary");
            if (Over())
                ShrinkToolResults(messages, trimmed, Over);
            if (Over())
                trimmed.Add("still over the cap");
        }

        return new InputFit(Build(), kept);
    }

    private static int DropRecalled(List<(string Part, string Text)> sections, List<string> trimmed, Func<bool> over, int expected)
    {
        var at = sections.FindIndex(s => s.Part == PromptPart.Recalled);
        if (at < 0)
            return expected;

        var lines = sections[at].Text.TrimEnd().Split('\n');
        var firstItem = Array.FindIndex(lines, l => MemoryItem().IsMatch(l));
        var items = new List<string>();
        if (firstItem >= 0)
        {
            foreach (var line in lines.Skip(firstItem))
            {
                if (MemoryItem().IsMatch(line))
                    items.Add(line);
                else
                    items[^1] += "\n" + line;
            }
        }

        if (firstItem < 0 || items.Count != expected)
        {
            sections.RemoveAt(at);
            trimmed.Add("recalled memories");
            return 0;
        }

        var head = string.Join('\n', lines.Take(firstItem));
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
        return items.Count;
    }

    private static void ShrinkToolResults(List<Message> messages, List<string> trimmed, Func<bool> over)
    {
        var candidates = Enumerable.Range(0, Math.Max(0, messages.Count - KeepRecentMessages))
            .Where(i => messages[i].Role == Role.Tool && messages[i].Content.Length > ToolResultKeepChars + ToolResultCut.Length)
            .OrderByDescending(i => messages[i].Content.Length)
            .ToList();

        var count = 0;
        foreach (var index in candidates)
        {
            messages[index] = messages[index] with { Content = messages[index].Content[..ToolResultKeepChars] + ToolResultCut };
            count++;
            if (!over())
                break;
        }

        if (count > 0)
            trimmed.Add($"{count} tool result{(count == 1 ? "" : "s")}");
    }
}
