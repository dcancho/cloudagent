using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Indexing;
using CloudAgent.Core.Memory;

namespace CloudAgent.Core.Loop;

/// <summary>What a compaction pass did.</summary>
/// <param name="Evicted">Turns removed from the working window.</param>
/// <param name="Consolidated">Durable facts learned from those turns.</param>
public readonly record struct CompactionResult(int Evicted, int Consolidated);

/// <summary>
/// Keeps working memory under a token budget. When the context grows too large it evicts the
/// oldest turns, folds them into a rolling summary (via the <see cref="Summarizer"/>), and —
/// if a <see cref="MemoryManager"/> is supplied — archives the evicted turns as episodic
/// memory so nothing is truly lost.
/// </summary>
public sealed class ContextCompactor(
    Summarizer summarizer,
    AgentOptions options,
    MemoryManager? memory = null,
    Consolidator? consolidator = null,
    StateSnapshotter? snapshotter = null)
{
    private const string RollingInstruction =
        "Update the running summary of this conversation. Preserve decisions made, facts " +
        "established, and open threads. Be concise and factual; no preamble.";

    /// <summary>
    /// Compact the context if it exceeds the budget: evict oldest turns, refresh the rolling
    /// summary, archive the evicted turns as episodic memory, and consolidate durable facts
    /// from them. Returns how much happened.
    /// </summary>
    public async Task<CompactionResult> CompactIfNeededAsync(
        AgentContext context, CancellationToken cancellationToken = default)
    {
        if (EstimateTokens(context) <= options.WorkingMemoryBudgetTokens)
            return default;

        var evicted = context.EvictOldest(options.KeepRecentMessages);
        if (evicted.Count == 0)
            return default;

        var rendered = Render(context.RollingSummary, evicted);
        context.RollingSummary = await summarizer
            .SummarizeAsync(rendered, RollingInstruction, cancellationToken)
            .ConfigureAwait(false);

        // Update (not summarize) the operational-state snapshot with the departing turns.
        if (snapshotter is not null)
            context.State = await snapshotter
                .UpdateAsync(context.State, evicted, cancellationToken)
                .ConfigureAwait(false);

        if (memory is not null)
            await memory.ArchiveAsync(evicted, cancellationToken).ConfigureAwait(false);

        var consolidated = 0;
        if (consolidator is not null)
        {
            var facts = await consolidator.ConsolidateAsync(evicted, cancellationToken).ConfigureAwait(false);
            consolidated = facts.Count;
        }

        return new CompactionResult(evicted.Count, consolidated);
    }

    /// <summary>Rough token estimate (~4 chars/token) over the summary, state, and remaining turns.</summary>
    private static int EstimateTokens(AgentContext context)
    {
        var chars = context.RollingSummary?.Length ?? 0;
        chars += context.State.Render().Length;
        foreach (var message in context.Messages)
            chars += message.Content.Length + 8; // small per-message overhead
        return chars / 4;
    }

    private static string Render(string? existingSummary, IReadOnlyList<Message> evicted)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingSummary))
            sb.AppendLine("Previous summary:").AppendLine(existingSummary).AppendLine();

        sb.AppendLine("Newer messages to fold in:");
        foreach (var message in evicted)
            sb.AppendLine($"{message.Role}: {message.Content}");

        return sb.ToString();
    }
}
