using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Memory;

/// <summary>
/// The "learning" pass: looks at conversation turns that are about to be summarized away and
/// asks the LLM to extract durable, general facts — classifying each as personal / project /
/// session — then writes the new ones to memory. Runs during compaction so learning happens
/// exactly when information would otherwise be lost. Near-duplicates are skipped.
/// </summary>
public sealed class Consolidator(
    ILlmProvider provider,
    MemoryManager memory,
    string? model = null,
    double dedupeThreshold = 0.92)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string Instruction =
        "From the conversation excerpt below, extract any DURABLE, GENERAL facts worth " +
        "remembering for future sessions. Classify each fact's scope:\n" +
        "- personal: about the user or how they like to work (preferences, identity)\n" +
        "- project: about this codebase/workspace (architecture, decisions, conventions, constraints)\n" +
        "- session: only relevant to the current task\n" +
        "Ignore small talk and transient details. Each fact must be a self-contained statement.\n" +
        "Return ONLY a JSON array of objects {\"scope\":\"...\",\"fact\":\"...\",\"importance\":0-100}. " +
        "Return [] if nothing is durable.";

    /// <summary>Extract durable facts from the given turns and store the new ones.</summary>
    public async Task<IReadOnlyList<MemoryEntry>> ConsolidateAsync(
        IReadOnlyList<Message> messages, CancellationToken cancellationToken = default)
    {
        var transcript = RenderTranscript(messages);
        if (string.IsNullOrWhiteSpace(transcript))
            return [];

        var candidates = await ExtractAsync(transcript, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
            return [];

        var stored = new List<MemoryEntry>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Fact))
                continue;
            if (!Enum.TryParse<MemoryScope>(candidate.Scope, ignoreCase: true, out var scope))
                scope = MemoryScope.Project;
            if (await IsDuplicateAsync(candidate.Fact, scope, cancellationToken).ConfigureAwait(false))
                continue;

            var importance = Math.Clamp(candidate.Importance, 0, 100) / 100.0;
            stored.Add(await memory
                .RememberAsync(candidate.Fact, scope, importance, "consolidation", cancellationToken)
                .ConfigureAwait(false));
        }

        return stored;
    }

    private async Task<IReadOnlyList<FactCandidate>> ExtractAsync(
        string transcript, CancellationToken cancellationToken)
    {
        var request = new LlmRequest
        {
            System = "You extract durable facts from a conversation. Output only a JSON array, no prose.",
            Messages = [Message.User($"{Instruction}\n\n---\n{transcript}")],
            Model = model,
            MaxTokens = 800,
            Temperature = 0.1,
        };

        LlmResponse response;
        try
        {
            response = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return []; // consolidation is best-effort; a provider hiccup shouldn't break compaction
        }

        var json = ExtractJsonArray(response.Text);
        if (json is null)
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<FactCandidate>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<bool> IsDuplicateAsync(string fact, MemoryScope scope, CancellationToken cancellationToken)
    {
        var hits = await memory.RecallAsync(fact, [scope], 1, cancellationToken).ConfigureAwait(false);
        return hits.Count > 0 && hits[0].Score >= dedupeThreshold;
    }

    private static string RenderTranscript(IReadOnlyList<Message> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages.Where(m => !string.IsNullOrWhiteSpace(m.Content)))
            sb.AppendLine($"{message.Role}: {message.Content}");
        return sb.ToString();
    }

    /// <summary>Pull the JSON array out of a response that may be wrapped in prose or fences.</summary>
    private static string? ExtractJsonArray(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private sealed record FactCandidate(
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("fact")] string Fact,
        [property: JsonPropertyName("importance")] int Importance);
}
