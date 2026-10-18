using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Memory;

/// <summary>What recall found for a turn.</summary>
/// <param name="Hits">Active memories to inject in full.</param>
/// <param name="ColdStubs">Cold memories that ranked among the top matches, to mention as stubs
/// the agent can restore; never injected in full.</param>
public sealed record RecallResult(IReadOnlyList<MemoryHit> Hits, IReadOnlyList<MemoryHit> ColdStubs)
{
    public static RecallResult Empty { get; } = new([], []);
}

/// <summary>
/// The read-side pipeline: rewrite the query (the question rarely resembles the answer),
/// run one wide-and-cheap first pass (the store's hybrid BM25 + vector search), then one
/// narrow-and-expensive second pass (LLM rerank), and finally trim to a strict budget —
/// fewer, better memories beat many mediocre ones (context rot). Rewriter and reranker are
/// optional: without them this degrades to the plain topK + threshold selection.
/// </summary>
public sealed class MemoryRetriever(
    MemoryManager memory,
    QueryRewriter? rewriter = null,
    MemoryReranker? reranker = null)
{
    /// <summary>Width of the first pass — cheap, so err generous; the second pass narrows.</summary>
    private const int WideCandidates = 30;

    /// <summary>Rough per-hit formatting overhead (bullet, scope/kind tag) in characters.</summary>
    private const int PerHitOverheadChars = 24;

    /// <summary>The underlying manager, for reads outside the pipeline (e.g. resident memories).</summary>
    public MemoryManager Memory => memory;

    /// <summary>
    /// Retrieve the memories to inject for a turn. <paramref name="topK"/> and
    /// <paramref name="threshold"/> drive the fallback selection when no reranker decides;
    /// <paramref name="budgetChars"/> caps the total injected content (0 = unlimited).
    /// <paramref name="light"/> skips the query rewrite and the rerank, the two model calls.
    /// <paramref name="coldStubLimit"/> lets Cold memories compete in the first pass: any that rank
    /// within the top-K and score at least <paramref name="coldStubMinScore"/> come back as stubs, at
    /// most this many (0 = Cold stays out of recall).
    /// </summary>
    public async Task<RecallResult> RetrieveAsync(
        string query,
        IReadOnlyCollection<MemoryScope> scopes,
        int topK,
        double threshold,
        int budgetChars,
        bool light = false,
        int coldStubLimit = 0,
        double coldStubMinScore = 0,
        CancellationToken cancellationToken = default)
    {
        var searchQuery = rewriter is null || light
            ? query
            : await rewriter.RewriteAsync(query, cancellationToken).ConfigureAwait(false);

        var ranked = await memory
            .RecallAsync(searchQuery, scopes, WideCandidates, cancellationToken, includeCold: coldStubLimit > 0)
            .ConfigureAwait(false);
        if (ranked.Count == 0)
            return RecallResult.Empty;

        var stubs = ranked
            .Take(topK)
            .Where(hit => hit.Entry.Residency == MemoryResidency.Cold && hit.Score >= coldStubMinScore)
            .Take(coldStubLimit)
            .ToArray();

        var candidates = ranked.Where(hit => hit.Entry.Residency != MemoryResidency.Cold).ToArray();
        if (candidates.Length == 0)
            return new RecallResult([], stubs);

        var selected = await SelectAsync(query, candidates, topK, threshold, useReranker: !light, cancellationToken)
            .ConfigureAwait(false);

        return new RecallResult(ApplyBudget(selected, budgetChars), stubs);
    }

    private async Task<IReadOnlyList<MemoryHit>> SelectAsync(
        string query,
        IReadOnlyList<MemoryHit> candidates,
        int topK,
        double threshold,
        bool useReranker,
        CancellationToken cancellationToken)
    {
        if (reranker is not null && useReranker)
        {
            // The reranker judges against the ORIGINAL turn, not the rewritten query — the
            // rewrite optimizes matching, but usefulness is about what the turn needs.
            var ranked = await reranker
                .RerankAsync(query, candidates.Select(hit => hit.Entry).ToArray(), topK, cancellationToken)
                .ConfigureAwait(false);

            // The reranker's verdict replaces the threshold; null means it failed, fall through.
            if (ranked is not null)
                return ranked.Select(i => candidates[i]).ToArray();
        }

        // Fallback selection: always the top-K, plus any others above the score threshold.
        var selected = new List<MemoryHit>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (i < topK || candidates[i].Score >= threshold)
                selected.Add(candidates[i]);
            else
                break;
        }

        return selected;
    }

    /// <summary>Keep hits (in order) until the character budget runs out; never drop the first.</summary>
    private static IReadOnlyList<MemoryHit> ApplyBudget(IReadOnlyList<MemoryHit> hits, int budgetChars)
    {
        if (budgetChars <= 0 || hits.Count == 0)
            return hits;

        var kept = new List<MemoryHit>();
        var used = 0;
        foreach (var hit in hits)
        {
            used += hit.Entry.Content.Length + PerHitOverheadChars;
            if (kept.Count > 0 && used > budgetChars)
                break;
            kept.Add(hit);
        }

        return kept;
    }
}
