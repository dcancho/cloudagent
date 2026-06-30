using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.Inference;
using Azure.Core;
using Azure.Identity;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Providers;

/// <summary>
/// Produces embeddings from the Microsoft Foundry catalog via the Azure AI Inference SDK
/// (<c>EmbeddingsClient</c>, the <c>/embeddings</c> route). Same endpoint and auth model as
/// <see cref="FoundryLlmProvider"/>.
/// </summary>
public sealed class FoundryEmbeddingProvider : IEmbeddingProvider
{
    private readonly EmbeddingsClient _client;
    private readonly string? _model;

    /// <param name="endpoint">
    /// The Foundry models endpoint, e.g. <c>https://&lt;resource&gt;.services.ai.azure.com/models</c>.
    /// </param>
    /// <param name="apiKey">API key, or null to use <see cref="DefaultAzureCredential"/> (Entra ID).</param>
    /// <param name="model">Embedding model to use (e.g. "text-embedding-3-small", "embed-v-4-0").</param>
    public FoundryEmbeddingProvider(Uri endpoint, string? apiKey = null, string? model = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var options = new AzureAIInferenceClientOptions();
        _client = string.IsNullOrWhiteSpace(apiKey)
            ? new EmbeddingsClient(endpoint, (TokenCredential)new DefaultAzureCredential(), options)
            : new EmbeddingsClient(endpoint, new AzureKeyCredential(apiKey), options);

        _model = model;
    }

    public string Name => "foundry";

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        if (inputs.Count == 0)
            return [];

        var options = new EmbeddingsOptions(inputs);
        if (!string.IsNullOrWhiteSpace(_model))
            options.Model = _model;

        Response<EmbeddingsResult> response =
            await _client.EmbedAsync(options, cancellationToken).ConfigureAwait(false);

        // Restore input order via each item's Index.
        var vectors = new float[inputs.Count][];
        foreach (var item in response.Value.Data)
            vectors[item.Index] = item.Embedding.ToObjectFromJson<float[]>()
                ?? throw new InvalidOperationException($"Empty embedding for input {item.Index}.");

        return vectors;
    }
}
