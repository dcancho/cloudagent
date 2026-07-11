using System;
using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Providers;

namespace CloudAgent.Host.Configuration;

/// <summary>
/// Builds the live LLM and embedding providers from a resolved <see cref="LlmConfig"/>. This is
/// the seam the composition root (and, later, subagent construction) uses instead of hand-rolling
/// <c>new FoundryLlmProvider(...)</c> with env lookups inline. Selection logic lives here once;
/// callers just ask for what they need.
/// </summary>
public sealed class ProviderFactory(LlmConfig config)
{
    public LlmConfig Config => config;

    /// <summary>The main-loop model id, for wiring into <c>AgentOptions.Model</c>.</summary>
    public string Model => config.Model;

    /// <summary>The background-job model id (summaries, judging, rewrite, rerank, consolidation).</summary>
    public string SmallModel => config.SmallModel;

    /// <summary>Mint the configured LLM provider. A fresh instance per call; providers are cheap
    /// and hold no cross-agent state, so subagents can safely get their own.</summary>
    public ILlmProvider CreateLlm() => config.Provider switch
    {
        ProviderKind.ClaudeCli => new ClaudeCliProvider(config.ClaudeModel),
        ProviderKind.Foundry => new FoundryLlmProvider(config.FoundryEndpoint!, config.FoundryApiKey, config.Model),
        ProviderKind.Anthropic => new AnthropicLlmProvider(config.AnthropicKey!),
        _ => new EchoLlmProvider(),
    };

    /// <summary>Mint the embedding provider: Foundry whenever an endpoint is set (pennies), else
    /// the deterministic offline hash — independent of which backend serves the LLM.</summary>
    public IEmbeddingProvider CreateEmbeddings() =>
        config.FoundryEndpoint is not null
            ? new FoundryEmbeddingProvider(config.FoundryEndpoint, config.FoundryApiKey, config.EmbeddingModel)
            : new HashEmbeddingProvider();
}
