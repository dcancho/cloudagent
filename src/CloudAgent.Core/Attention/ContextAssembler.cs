using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Loop;
using CloudAgent.Core.Memory;

namespace CloudAgent.Core.Attention;

/// <summary>
/// The default <see cref="IAttentionManager"/>: assembles a turn's prompt from the persona,
/// resident memories (standing in the cacheable system prefix), and the volatile per-turn context
/// — operational state, rolling summary, and memories recalled through the retrieval pipeline,
/// trimmed to budget. It records nothing: it returns the injected hits and lets the loop feed the
/// utility ledger, keeping attention (eje 5) off the learning path (eje 7). Rendering lives in
/// <see cref="AttentionRendering"/> so a metacognitive gate can re-render after suppressing hits.
/// </summary>
public sealed class ContextAssembler(
    string persona,
    MemoryRetriever? retriever,
    AttentionOptions options) : IAttentionManager
{
    /// <summary>Rough chars-per-token, to turn the token budget into the retriever's char budget.</summary>
    private const int CharsPerToken = 4;

    public async Task<AssembledContext> AssembleAsync(
        AgentContext context, string? query, CancellationToken cancellationToken = default)
    {
        var residents = await LoadResidentsAsync(cancellationToken).ConfigureAwait(false);
        var hits = await RecallAsync(query, cancellationToken).ConfigureAwait(false);

        var system = AttentionRendering.System(persona, residents);
        var ephemeral = AttentionRendering.Ephemeral(context.State.Render(), context.RollingSummary, hits);

        return new AssembledContext(system, ephemeral, hits);
    }

    /// <summary>The resident-stratum memories that stand in the system prefix; empty without memory.</summary>
    private async Task<IReadOnlyList<MemoryEntry>> LoadResidentsAsync(CancellationToken cancellationToken)
    {
        if (retriever is null)
            return [];

        return await retriever.Memory
            .GetResidentAsync(options.RecallScopes, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Recall the turn's memories through the pipeline (rewrite → wide hybrid → rerank →
    /// budget); empty when there is no retriever or no query.</summary>
    private async Task<IReadOnlyList<MemoryHit>> RecallAsync(string? query, CancellationToken cancellationToken)
    {
        if (retriever is null || string.IsNullOrWhiteSpace(query))
            return [];

        return await retriever
            .RetrieveAsync(
                query, options.RecallScopes, options.RecallTopK, options.RecallThreshold,
                budgetChars: options.RecallBudgetTokens * CharsPerToken, cancellationToken)
            .ConfigureAwait(false);
    }
}
