using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CloudAgent.Core.Loop;

/// <summary>
/// The structured snapshot of where the work stands — separate from the narrative rolling
/// summary, and never pruned. Compaction UPDATES it field by field (via the
/// <see cref="StateSnapshotter"/>) instead of summarizing it, so it cannot fog over with
/// repeated compactions the way a summary-of-a-summary would.
/// </summary>
public sealed class OperationalState
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>The user's current request, kept verbatim so intent never drifts in paraphrase.</summary>
    public string? ActiveTask { get; set; }

    /// <summary>Facts checked and confirmed true this session.</summary>
    public List<string> Verified { get; set; } = [];

    /// <summary>Approaches and hypotheses ruled out — so they are not retried.</summary>
    public List<string> Discarded { get; set; } = [];

    /// <summary>Decisions currently in force.</summary>
    public List<string> Decisions { get; set; } = [];

    /// <summary>Open blockers, each with the exact error text.</summary>
    public List<string> Blockers { get; set; } = [];

    /// <summary>Work still to do, stated declaratively (desired end state, not steps).</summary>
    public List<string> Remaining { get; set; } = [];

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ActiveTask)
        && Verified.Count == 0 && Discarded.Count == 0 && Decisions.Count == 0
        && Blockers.Count == 0 && Remaining.Count == 0;

    /// <summary>Markdown block for the system prompt; empty string when there is nothing yet.</summary>
    public string Render()
    {
        if (IsEmpty)
            return string.Empty;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(ActiveTask))
            sb.AppendLine($"Active task (user's words, verbatim): {ActiveTask}");

        AppendSection(sb, "Verified", Verified);
        AppendSection(sb, "Discarded (do not retry)", Discarded);
        AppendSection(sb, "Decisions in force", Decisions);
        AppendSection(sb, "Blockers", Blockers);
        AppendSection(sb, "Remaining work", Remaining);
        return sb.ToString().TrimEnd();
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parse a snapshot from model output; null when the JSON is unusable.</summary>
    public static OperationalState? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OperationalState>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AppendSection(StringBuilder sb, string title, List<string> items)
    {
        if (items.Count == 0)
            return;

        sb.AppendLine($"{title}:");
        foreach (var item in items.Where(i => !string.IsNullOrWhiteSpace(i)))
            sb.AppendLine($"- {item}");
    }
}
