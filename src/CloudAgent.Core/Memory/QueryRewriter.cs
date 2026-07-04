using System;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Memory;

/// <summary>
/// Reformulates a conversational turn into a retrieval query before searching — because the
/// question rarely resembles the answer: "how do I deploy this?" should search like the
/// stored fact reads ("deploys run via ..."). One cheap call per recall. Best-effort: any
/// failure falls back to the original text, which is what we'd have searched anyway.
/// </summary>
public sealed class QueryRewriter(ILlmProvider provider, string? model = null)
{
    /// <summary>Cap on the turn text so a rewrite call stays cheap.</summary>
    private const int MaxInputChars = 2000;

    private const string Instruction =
        "The memory store holds remembered facts and past-conversation excerpts. Rewrite the " +
        "user turn below as the search query most likely to match relevant stored memories — " +
        "phrase it the way the stored fact would read, not the way the question is asked. " +
        "Keep exact names, commands, and identifiers verbatim. Return ONLY the query.\n\nTurn:\n";

    public async Task<string> RewriteAsync(string userText, CancellationToken cancellationToken = default)
    {
        var clipped = userText.Length <= MaxInputChars ? userText : userText[..MaxInputChars];

        var request = new LlmRequest
        {
            System = "You rewrite conversational turns into search queries. Output only the query, no prose.",
            Messages = [Message.User(Instruction + clipped)],
            Model = model,
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
            return userText;
        }

        var query = response.Text.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(query) ? userText : query.ReplaceLineEndings(" ");
    }
}
