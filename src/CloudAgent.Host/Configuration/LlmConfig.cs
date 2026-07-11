using System;

namespace CloudAgent.Host.Configuration;

/// <summary>Which LLM backend the agent talks to. Selected by environment (see
/// <see cref="LlmConfig.FromEnvironment"/>); the order encodes precedence.</summary>
public enum ProviderKind
{
    /// <summary>Ride a Claude Pro/Max account through the local `claude -p` CLI.</summary>
    ClaudeCli,

    /// <summary>The Microsoft Foundry model catalog (one endpoint, model per request).</summary>
    Foundry,

    /// <summary>The Anthropic Messages API directly.</summary>
    Anthropic,

    /// <summary>The offline echo fake — no config, always available.</summary>
    Echo,
}

/// <summary>
/// The resolved LLM/provider configuration, read once from the environment and passed around as
/// data. Keeping this out of the composition root means the ugly env-reading + precedence logic
/// lives in one named, testable place — and a <see cref="ProviderFactory"/> built from it can
/// mint providers anywhere (root agent, subagents), not just at startup.
/// </summary>
/// <param name="Provider">Which backend won the precedence check.</param>
/// <param name="Model">Main-loop model id. Empty string means "let the provider decide"
/// (the Claude CLI omits <c>--model</c> and uses the account default).</param>
/// <param name="SmallModel">Model for background jobs (summaries, judging, rewrite, rerank,
/// consolidation). Defaults to <see cref="Model"/> when <c>AGENT_SMALL_MODEL</c> is unset.</param>
/// <param name="AnthropicKey">API key for the direct Anthropic provider, if configured.</param>
/// <param name="FoundryEndpoint">Foundry models endpoint. When set, embeddings use Foundry too —
/// independent of which <see cref="Provider"/> serves the LLM.</param>
/// <param name="FoundryApiKey">Key for Foundry; null falls back to Entra ID credentials.</param>
/// <param name="EmbeddingModel">Foundry embedding model id (ignored when there is no endpoint).</param>
/// <param name="ClaudeModel">Raw <c>CLAUDE_MODEL</c> passed to the CLI provider; null = default.</param>
public sealed record LlmConfig(
    ProviderKind Provider,
    string Model,
    string SmallModel,
    string? AnthropicKey,
    Uri? FoundryEndpoint,
    string? FoundryApiKey,
    string EmbeddingModel,
    string? ClaudeModel)
{
    /// <summary>Model used when nothing more specific is configured (direct Anthropic / echo).</summary>
    public const string DefaultModel = "claude-opus-4-8";

    /// <summary>Foundry model used when the catalog is reachable but none is named.</summary>
    public const string DefaultFoundryModel = "gpt-4o-mini";

    /// <summary>Foundry embedding model used when none is named.</summary>
    public const string DefaultEmbeddingModel = "text-embedding-3-small";

    /// <summary>
    /// Resolve the configuration from environment variables. Precedence, highest first:
    /// Claude CLI (<c>CLAUDE_CLI=1</c>) → Foundry (<c>FOUNDRY_ENDPOINT</c>) →
    /// Anthropic (<c>ANTHROPIC_API_KEY</c>) → echo. The main model depends on the winner;
    /// embeddings and the background model are orthogonal to it.
    /// </summary>
    public static LlmConfig FromEnvironment()
    {
        var useClaudeCli = string.Equals(
            Environment.GetEnvironmentVariable("CLAUDE_CLI"), "1", StringComparison.Ordinal);
        var claudeModel = Environment.GetEnvironmentVariable("CLAUDE_MODEL");
        var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        var endpointRaw = Environment.GetEnvironmentVariable("FOUNDRY_ENDPOINT");
        var foundryEndpoint = string.IsNullOrWhiteSpace(endpointRaw) ? null : new Uri(endpointRaw);

        var (provider, model) =
            useClaudeCli ? (ProviderKind.ClaudeCli, claudeModel ?? "")
            : foundryEndpoint is not null
                ? (ProviderKind.Foundry, Environment.GetEnvironmentVariable("FOUNDRY_MODEL") ?? DefaultFoundryModel)
            : !string.IsNullOrWhiteSpace(anthropicKey)
                ? (ProviderKind.Anthropic, DefaultModel)
            : (ProviderKind.Echo, DefaultModel);

        // Background jobs fall back to the main model unless explicitly cheapened.
        var smallModel = Environment.GetEnvironmentVariable("AGENT_SMALL_MODEL") ?? model;

        return new LlmConfig(
            provider,
            model,
            smallModel,
            anthropicKey,
            foundryEndpoint,
            Environment.GetEnvironmentVariable("FOUNDRY_API_KEY"),
            Environment.GetEnvironmentVariable("FOUNDRY_EMBED_MODEL") ?? DefaultEmbeddingModel,
            claudeModel);
    }
}
