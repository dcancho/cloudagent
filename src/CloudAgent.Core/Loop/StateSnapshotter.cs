using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Loop;

/// <summary>
/// Keeps the <see cref="OperationalState"/> current: when turns are evicted, it feeds the
/// existing snapshot plus the departing turns to the LLM and gets back an updated snapshot —
/// an iterative field-by-field edit, never a re-summarization. Best-effort: on any failure
/// the previous snapshot is kept, which is always safe (merely stale).
/// </summary>
public sealed class StateSnapshotter(ILlmProvider provider, string? model = null)
{
    private const string Instruction =
        "Update the operational-state snapshot using the conversation excerpt below. The " +
        "snapshot is the only context that survives after these messages are forgotten, so " +
        "carry facts INTO it — remove an item only when the excerpt shows it was resolved or " +
        "superseded. Fields:\n" +
        "- activeTask: the user's current request, quoted VERBATIM from their message " +
        "(replace only if a newer request supersedes it)\n" +
        "- verified: facts that were checked and confirmed\n" +
        "- discarded: approaches or hypotheses ruled out, so they are not retried\n" +
        "- decisions: decisions currently in force\n" +
        "- blockers: open blockers, each with the exact error text\n" +
        "- remaining: work still to do, stated declaratively (desired end state, not steps)\n" +
        "Return ONLY the updated JSON object with exactly those keys.";

    public async Task<OperationalState> UpdateAsync(
        OperationalState current,
        IReadOnlyList<Message> evicted,
        CancellationToken cancellationToken = default)
    {
        var transcript = RenderTranscript(evicted);
        if (string.IsNullOrWhiteSpace(transcript))
            return current;

        var request = new LlmRequest
        {
            System = "You maintain a structured operational-state snapshot for an agent. " +
                     "Output only a JSON object, no prose.",
            Messages =
            [
                Message.User(
                    $"{Instruction}\n\nCurrent snapshot:\n{current.ToJson()}\n\n" +
                    $"Conversation excerpt being forgotten:\n{transcript}"),
            ],
            Model = model,
            MaxTokens = 1000,
            Temperature = 0.1,
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
            return current; // stale is safe; broken is not
        }

        var json = ExtractJsonObject(response.Text);
        if (json is null)
            return current;

        return OperationalState.FromJson(json) ?? current;
    }

    private static string RenderTranscript(IReadOnlyList<Message> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages.Where(m => !string.IsNullOrWhiteSpace(m.Content)))
            sb.AppendLine($"{message.Role}: {message.Content}");
        return sb.ToString();
    }

    /// <summary>Pull the JSON object out of a response that may be wrapped in prose or fences.</summary>
    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }
}
