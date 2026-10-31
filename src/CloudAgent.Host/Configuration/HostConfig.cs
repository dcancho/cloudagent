using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CloudAgent.Host.Configuration;

/// <summary>
/// Everything the host resolves from the environment at startup, as data. This is the umbrella
/// over <see cref="LlmConfig"/> (which stays a cohesive, independently-testable unit) plus the
/// workspace, surface tokens, and memory/sleep knobs. Reading env lives here once; the
/// composition root keeps only construction — it never touches <c>Environment</c> again.
/// </summary>
/// <param name="Llm">Provider/model configuration (see <see cref="LlmConfig"/>).</param>
/// <param name="WorkspaceRoot">Rooted area the agent may act within (<c>AGENT_WORKSPACE</c>).</param>
/// <param name="TavilyApiKey">Web-search key; null leaves search unconfigured.</param>
/// <param name="PersonalMemoryDirectory">Where each person's personal memory database lives, one file per
/// person, shared by every workspace (<c>PERSONAL_MEMORY_DIR</c>).</param>
/// <param name="PeoplePath">The people the agent talks to and their linked accounts, which is also the
/// allowlist for remote surfaces (<c>PEOPLE_FILE</c>).</param>
/// <param name="ConsolidationPolicyPath">Resolved path to the consolidation policy doc
/// (<c>CONSOLIDATION_POLICY</c>); the host reads it with a built-in fallback if absent.</param>
/// <param name="MemoryBudgetTokens">Working-memory compaction budget (<c>AGENT_MEMORY_BUDGET_TOKENS</c>).</param>
/// <param name="SleepIntervalMinutes">Consolidation cadence (<c>SLEEP_INTERVAL_MIN</c>); ≤0 disables.</param>
/// <param name="TelegramBotToken">Telegram surface token; null skips the surface.</param>
/// <param name="DiscordBotToken">Discord surface token; null skips the surface.</param>
/// <param name="WebchatEnabled">Whether to start the web-chat surface (<c>WEBCHAT=1</c>).</param>
/// <param name="WebchatPort">Web-chat port (<c>WEBCHAT_PORT</c>) when enabled.</param>
/// <param name="WebchatAnonymous">Whether a web-chat connection without Tailscale identity gets in, unidentified
/// (<c>WEBCHAT_ANONYMOUS=1</c>). Every such connection is the same nobody and shares its conversations, so this
/// is for a web chat only this machine's user can reach.</param>
/// <param name="ConsoleEnabled">Whether to start the console surface (<c>CLI</c>, on unless <c>0</c>); off for a host
/// with no terminal, such as a service.</param>
/// <param name="TurnTakingEnabled">Group messages into bursts before the agent answers (<c>TURN_TAKING</c>, on unless <c>0</c>).</param>
/// <param name="ContextRouting">How each turn's pipeline is chosen (<c>CONTEXT_ROUTING</c>): <c>off</c> runs all of it,
/// <c>decision</c> asks the Foundry decision model, <c>generative</c> asks the background chat model. Defaults to
/// <c>decision</c> when a decision endpoint is configured, otherwise <c>off</c>.</param>
/// <param name="DecisionsEndpoint">Foundry decision API endpoint (<c>FOUNDRY_DECISIONS_ENDPOINT</c>).</param>
/// <param name="DecisionsModel">Decision model deployment name (<c>DECISIONS_MODEL</c>).</param>
/// <param name="CodeRepos">Repositories a coding agent may work on (<c>CODE_REPOS</c>, <c>name=path</c> pairs
/// separated by <c>;</c>).</param>
/// <param name="LearnIdleMinutes">How long a conversation must be quiet before the sleep timer learns from it
/// (<c>LEARN_IDLE_MIN</c>).</param>
/// <param name="TokenBudget">Tokens, input and output together, the workspace's agents may spend before they
/// stop (<c>TOKEN_BUDGET</c>); null means no limit. Spending is kept in the workspace across restarts.</param>
/// <param name="CodeCommands">Programs a coding agent may run in them (<c>CODE_COMMANDS</c>, comma separated).</param>
public sealed record HostConfig(
    LlmConfig Llm,
    string WorkspaceRoot,
    string? TavilyApiKey,
    string PersonalMemoryDirectory,
    string PeoplePath,
    string ConsolidationPolicyPath,
    int MemoryBudgetTokens,
    int SleepIntervalMinutes,
    string? TelegramBotToken,
    string? DiscordBotToken,
    bool WebchatEnabled,
    int WebchatPort,
    bool WebchatAnonymous,
    bool ConsoleEnabled,
    bool TurnTakingEnabled,
    string ContextRouting,
    Uri? DecisionsEndpoint,
    string? DecisionsModel,
    IReadOnlyList<CodeRepo> CodeRepos,
    IReadOnlyList<string> CodeCommands,
    long? TokenBudget,
    int LearnIdleMinutes)
{
    public static readonly IReadOnlyList<string> DefaultCodeCommands = ["dotnet", "git", "npm", "node"];

    public const int DefaultMemoryBudgetTokens = 8000;
    public const int DefaultSleepIntervalMinutes = 30;
    public const int DefaultWebchatPort = 5080;
    public const int DefaultLearnIdleMinutes = 2;

    /// <summary>Resolve the full host configuration from environment variables and defaults.</summary>
    public static HostConfig FromEnvironment()
    {
        var workspaceRoot = Environment.GetEnvironmentVariable("AGENT_WORKSPACE")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "workspace");

        var policyPath = Environment.GetEnvironmentVariable("CONSOLIDATION_POLICY")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "docs", "consolidation-policy.md");

        var budget = int.TryParse(Environment.GetEnvironmentVariable("AGENT_MEMORY_BUDGET_TOKENS"), out var b)
            ? b
            : DefaultMemoryBudgetTokens;

        var sleep = int.TryParse(Environment.GetEnvironmentVariable("SLEEP_INTERVAL_MIN"), out var sm)
            ? sm
            : DefaultSleepIntervalMinutes;

        var webchatEnabled = string.Equals(
            Environment.GetEnvironmentVariable("WEBCHAT"), "1", StringComparison.Ordinal);
        var webchatPort = int.TryParse(Environment.GetEnvironmentVariable("WEBCHAT_PORT"), out var wp)
            ? wp
            : DefaultWebchatPort;

        var decisionsEndpoint = Uri.TryCreate(
            Environment.GetEnvironmentVariable("FOUNDRY_DECISIONS_ENDPOINT"), UriKind.Absolute, out var de) ? de : null;
        var decisionsModel = Environment.GetEnvironmentVariable("DECISIONS_MODEL");
        var routing = Environment.GetEnvironmentVariable("CONTEXT_ROUTING")?.Trim().ToLowerInvariant()
            ?? (decisionsEndpoint is not null && !string.IsNullOrWhiteSpace(decisionsModel) ? "decision" : "off");

        return new HostConfig(
            LlmConfig.FromEnvironment(),
            workspaceRoot,
            Environment.GetEnvironmentVariable("TAVILY_API_KEY"),
            Environment.GetEnvironmentVariable("PERSONAL_MEMORY_DIR")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloudagent", "memory", "people"),
            Environment.GetEnvironmentVariable("PEOPLE_FILE")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloudagent", "people.json"),
            policyPath,
            budget,
            sleep,
            Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"),
            Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN"),
            webchatEnabled,
            webchatPort,
            string.Equals(Environment.GetEnvironmentVariable("WEBCHAT_ANONYMOUS"), "1", StringComparison.Ordinal),
            !string.Equals(Environment.GetEnvironmentVariable("CLI"), "0", StringComparison.Ordinal),
            !string.Equals(Environment.GetEnvironmentVariable("TURN_TAKING"), "0", StringComparison.Ordinal),
            routing,
            decisionsEndpoint,
            decisionsModel,
            CodeRepo.Parse(Environment.GetEnvironmentVariable("CODE_REPOS")),
            Environment.GetEnvironmentVariable("CODE_COMMANDS") is { Length: > 0 } commands
                ? commands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : DefaultCodeCommands,
            long.TryParse(Environment.GetEnvironmentVariable("TOKEN_BUDGET"), out var tokens) && tokens > 0 ? tokens : null,
            int.TryParse(Environment.GetEnvironmentVariable("LEARN_IDLE_MIN"), out var idle) && idle >= 0 ? idle : DefaultLearnIdleMinutes);
    }
}

/// <summary>A repository a coding agent may work on, by the name people choose it with.</summary>
public sealed record CodeRepo(string Name, string Path)
{
    /// <summary>Parse <c>name=path</c> pairs separated by <c>;</c>; malformed pairs are skipped.</summary>
    public static IReadOnlyList<CodeRepo> Parse(string? value) =>
        (value ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            .Select(parts => new CodeRepo(parts[0], parts[1]))
            .ToList();
}
