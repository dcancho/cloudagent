using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Indexing;

/// <summary>
/// Generates summaries through the LLM provider. Used to condense a file (or a set of
/// search hits) into a short description — the bridge from raw matches to useful memory.
/// </summary>
public sealed class Summarizer(ILlmProvider provider, string? model = null)
{
    /// <summary>Cap on input text so a single summary call can't blow the context window.</summary>
    private const int MaxInputChars = 12_000;

    public async Task<string> SummarizeAsync(
        string text, string? instruction = null, CancellationToken cancellationToken = default)
    {
        var trimmed = text.Length > MaxInputChars ? text[..MaxInputChars] + "\n…(truncated)" : text;
        instruction ??= "Summarize the following content concisely, capturing its purpose and key points.";

        var request = new LlmRequest
        {
            System = "You are a precise summarizer. Produce a compact, faithful summary. No preamble.",
            Messages = [Message.User($"{instruction}\n\n---\n{trimmed}")],
            Model = model,
            MaxTokens = 512,
            Temperature = 0.2,
        };

        var response = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Text.Trim();
    }
}
