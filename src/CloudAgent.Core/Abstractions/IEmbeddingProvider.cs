using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Abstractions;

/// <summary>
/// The seam to an embedding backend. Turns text into vectors so the agent can match by
/// meaning (cosine similarity) rather than keywords. Same pattern as <see cref="ILlmProvider"/>:
/// the index and tools depend on this interface, not on any particular embedding service.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Short name for diagnostics (e.g. "foundry", "hash").</summary>
    string Name { get; }

    /// <summary>Embed a batch of texts, returning one vector per input in order.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs, CancellationToken cancellationToken = default);
}

public static class EmbeddingProviderExtensions
{
    /// <summary>Embed a single text.</summary>
    public static async Task<float[]> EmbedAsync(
        this IEmbeddingProvider provider, string input, CancellationToken cancellationToken = default)
    {
        var vectors = await provider.EmbedAsync([input], cancellationToken).ConfigureAwait(false);
        return vectors[0];
    }
}
