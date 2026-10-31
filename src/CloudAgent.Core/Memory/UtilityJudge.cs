using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Memory;

/// <summary>
/// The post-turn attribution call: given the memories that were injected into a turn and the
/// reply the agent produced, asks a (cheap) model which memories genuinely influenced the
/// reply — as opposed to being merely topically similar. Best-effort by design: a judging
/// hiccup must never affect the turn itself.
/// </summary>
public sealed class UtilityJudge(ILlmProvider provider, string? model = null)
{
    /// <summary>Caps on prompt inputs so a judging call stays cheap.</summary>
    private const int MaxMemoryChars = 300;
    private const int MaxTextChars = 2000;

    private const string Instruction =
        "These memories were injected into an assistant's context before it replied:\n\n{0}\n" +
        "\nUser request:\n{1}\n" +
        "\nAssistant's reply:\n{2}\n" +
        "\nWhich memories (by number) genuinely INFLUENCED the reply — shaped its content, " +
        "constrained a choice, or supplied a fact it used? Being on the same topic is not " +
        "influence. Return ONLY a JSON array of the influential numbers, e.g. [1,3]. " +
        "Return [] if none.";

    /// <summary>
    /// Judge which of <paramref name="injected"/> influenced <paramref name="assistantText"/>.
    /// Returns the zero-based indices of the useful ones; empty when the model says none helped;
    /// null when there was nothing to judge or the call failed, so no verdict gets recorded.
    /// </summary>
    public async Task<IReadOnlyList<int>?> JudgeAsync(
        IReadOnlyList<MemoryEntry> injected,
        string? userText,
        string assistantText,
        CancellationToken cancellationToken = default)
    {
        if (injected.Count == 0 || string.IsNullOrWhiteSpace(assistantText))
            return null;

        var numbered = new StringBuilder();
        for (var i = 0; i < injected.Count; i++)
            numbered.AppendLine($"{i + 1}. {Clip(injected[i].Content, MaxMemoryChars)}");

        var request = new LlmRequest
        {
            System = "You audit whether injected memories influenced an assistant's reply. " +
                     "Output only a JSON array of integers, no prose.",
            Messages =
            [
                Message.User(string.Format(
                    Instruction,
                    numbered,
                    Clip(userText ?? "(none)", MaxTextChars),
                    Clip(assistantText, MaxTextChars))),
            ],
            Model = model,
            Purpose = LlmPurpose.Judge,
            MaxTokens = 100,
            Temperature = 0,
        };

        LlmResponse response;
        try
        {
            response = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        return Parse(response.Text, injected.Count);
    }

    /// <summary>Parse the 1-based numbers out of the reply into 0-based indices; null when there is no array.</summary>
    private static IReadOnlyList<int>? Parse(string text, int count)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            return null;

        try
        {
            var numbers = JsonSerializer.Deserialize<List<int>>(text[start..(end + 1)]) ?? [];
            return numbers
                .Where(n => n >= 1 && n <= count)
                .Select(n => n - 1)
                .Distinct()
                .ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
