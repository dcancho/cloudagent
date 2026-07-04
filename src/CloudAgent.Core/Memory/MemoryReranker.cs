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
/// The narrow, expensive second stage of retrieval: given the wide hybrid candidates, one
/// cheap-model call picks the few that would genuinely help this turn, in usefulness order.
/// Returns null when the call fails (caller falls back to first-stage order) — distinct from
/// an empty result, which is the model saying "none of these help, inject nothing".
/// </summary>
public sealed class MemoryReranker(ILlmProvider provider, string? model = null)
{
    /// <summary>Caps on prompt inputs so a rerank call stays cheap.</summary>
    private const int MaxCandidateChars = 300;
    private const int MaxQueryChars = 1000;

    private const string Instruction =
        "A memory system fetched candidate memories for this turn of a conversation.\n" +
        "\nTurn:\n{0}\n" +
        "\nCandidates:\n{1}" +
        "\nSelect the candidates that would GENUINELY help with this turn — at most {2}, most " +
        "useful first. Being on the same topic is not enough. Return ONLY a JSON array of " +
        "their numbers, e.g. [3,1]. Return [] if none would help.";

    /// <summary>
    /// Rank the useful subset of <paramref name="candidates"/> for <paramref name="query"/>.
    /// Returns at most <paramref name="keep"/> zero-based indices in usefulness order; empty
    /// when nothing helps; null when the call failed and first-stage order should stand.
    /// </summary>
    public async Task<IReadOnlyList<int>?> RerankAsync(
        string query,
        IReadOnlyList<MemoryEntry> candidates,
        int keep,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0 || keep <= 0)
            return [];

        var numbered = new StringBuilder();
        for (var i = 0; i < candidates.Count; i++)
            numbered.AppendLine($"{i + 1}. {Clip(candidates[i].Content, MaxCandidateChars)}");

        var request = new LlmRequest
        {
            System = "You rank retrieved memories by usefulness for a conversation turn. " +
                     "Output only a JSON array of integers, no prose.",
            Messages = [Message.User(string.Format(Instruction, Clip(query, MaxQueryChars), numbered, keep))],
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
            return null;
        }

        return Parse(response.Text, candidates.Count, keep);
    }

    /// <summary>Parse 1-based numbers into 0-based indices; null when there is no array at all.</summary>
    private static IReadOnlyList<int>? Parse(string text, int count, int keep)
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
                .Take(keep)
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
