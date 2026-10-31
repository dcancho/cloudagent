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

/// <summary>Thresholds for the sleep passes. Defaults are starting points, to be tuned from
/// what the utility ledger actually measures.</summary>
public sealed class SleepOptions
{
    /// <summary>Episodes older than this that never proved useful get demoted to Cold.</summary>
    public TimeSpan EpisodeTtl { get; init; } = TimeSpan.FromDays(14);

    /// <summary>Promotion to Resident requires at least this many injections…</summary>
    public int PromoteMinInjections { get; init; } = 3;

    /// <summary>…and at least this useful rate.</summary>
    public double PromoteMinRate { get; init; } = 0.5;

    /// <summary>Residents whose rate falls below this (with enough data) drop back to Active.</summary>
    public double DemoteResidentRate { get; init; } = 0.25;

    /// <summary>Hard cap on Resident entries — the system prompt is expensive real estate.</summary>
    public int MaxResidents { get; init; } = 10;

    /// <summary>Distillation waits until at least this many Active episodes have accumulated.</summary>
    public int MinEpisodesToDistill { get; init; } = 20;

    /// <summary>Cap on episodes fed to one distillation call.</summary>
    public int MaxEpisodesPerPass { get; init; } = 60;
}

/// <summary>What one sleep pass did.</summary>
public readonly record struct SleepResult(int Demoted, int Promoted, string? StagedProposalId);

/// <summary>
/// The offline consolidation pass — what happens while nothing else is: replay recent
/// episodes and utility stats, then
/// <list type="bullet">
/// <item>mechanical moves (auto-applied, ledger-logged): demote what measured useless, promote
/// what measured valuable, within the strata ladder;</item>
/// <item>semantic distillation (staged, never auto-applied): collapse repeated episodes into
/// candidate principles + a dense summary, judged against the written consolidation policy.</item>
/// </list>
/// The policy document is the judge: it rides along as the system prompt of the distillation
/// call, so the criteria for generalizing are versioned prose, not buried weights.
/// </summary>
public sealed class SleepJob(
    ILlmProvider provider,
    MemoryManager memory,
    IUtilityLedger utility,
    ConsolidationLedger ledger,
    PendingConsolidations pending,
    string agentId,
    string policy,
    string? model = null,
    SleepOptions? options = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly MemoryScope[] AllScopes =
        [MemoryScope.Personal, MemoryScope.Project, MemoryScope.Session];

    private readonly SleepOptions _options = options ?? new SleepOptions();

    public async Task<SleepResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var entries = await memory.ListAsync(AllScopes, cancellationToken).ConfigureAwait(false);
        var stats = utility.Stats;
        var now = DateTimeOffset.UtcNow;

        var demoted = await DecayAsync(entries, stats, now, cancellationToken).ConfigureAwait(false);

        // Decay moved entries between strata; the later passes need the fresh view.
        if (demoted > 0)
            entries = await memory.ListAsync(AllScopes, cancellationToken).ConfigureAwait(false);

        var promoted = await RebalanceStrataAsync(entries, stats, cancellationToken).ConfigureAwait(false);
        var stagedId = await DistillAsync(entries, cancellationToken).ConfigureAwait(false);

        return new SleepResult(demoted, promoted, stagedId);
    }

    /// <summary>
    /// Forgetting as demotion: episodes past their TTL that never helped move to Cold. Reversible
    /// by design. Facts never leave Active here: being judged unused is a per-context verdict, which
    /// the attention gate acts on where it was measured.
    /// </summary>
    private async Task<int> DecayAsync(
        IReadOnlyList<MemoryEntry> entries,
        IReadOnlyDictionary<string, MemoryUtilityStats> stats,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var demoted = 0;
        foreach (var entry in entries.Where(e => e.Residency == MemoryResidency.Active))
        {
            var stat = stats.GetValueOrDefault(entry.Id);
            var reason = entry.Kind switch
            {
                MemoryKind.Episodic when now - (stat?.RestoredAt ?? entry.CreatedAt) > _options.EpisodeTtl
                                         && (stat is null || stat.Useful == 0) =>
                    $"episode past TTL ({_options.EpisodeTtl.TotalDays:0}d) with no measured usefulness",
                _ => null,
            };

            if (reason is null)
                continue;

            await memory.SetResidencyAsync(entry, MemoryResidency.Cold, cancellationToken).ConfigureAwait(false);
            await ledger.AppendAsync("demote", $"{entry.Id} -> Cold: {reason}", cancellationToken).ConfigureAwait(false);
            demoted++;
        }

        return demoted;
    }

    /// <summary>
    /// The generational ladder: facts that keep proving useful earn the system prompt;
    /// residents that stop earning it drop back to Active. Driven purely by measured utility.
    /// </summary>
    private async Task<int> RebalanceStrataAsync(
        IReadOnlyList<MemoryEntry> entries,
        IReadOnlyDictionary<string, MemoryUtilityStats> stats,
        CancellationToken cancellationToken)
    {
        var moves = 0;

        // Demotions first, to free room under the cap.
        foreach (var resident in entries.Where(e => e.Residency == MemoryResidency.Resident))
        {
            var stat = stats.GetValueOrDefault(resident.Id);
            if (stat is null || stat.Injections < _options.PromoteMinInjections
                || stat.UsefulRate >= _options.DemoteResidentRate)
                continue;

            await memory.SetResidencyAsync(resident, MemoryResidency.Active, cancellationToken).ConfigureAwait(false);
            await ledger.AppendAsync(
                "demote", $"{resident.Id} Resident -> Active: rate {stat.UsefulRate:0.00} below {_options.DemoteResidentRate}",
                cancellationToken).ConfigureAwait(false);
            moves++;
        }

        var residentCount = entries.Count(e => e.Residency == MemoryResidency.Resident);

        var candidates = entries
            .Where(e => e is { Residency: MemoryResidency.Active, Kind: MemoryKind.Fact })
            .Select(e => (Entry: e, Stat: stats.GetValueOrDefault(e.Id)))
            .Where(x => x.Stat is not null
                        && x.Stat.Injections >= _options.PromoteMinInjections
                        && x.Stat.UsefulRate >= _options.PromoteMinRate)
            .OrderByDescending(x => x.Stat!.UsefulRate)
            .ThenByDescending(x => x.Stat!.LastSeenAt);

        foreach (var (entry, stat) in candidates)
        {
            if (residentCount >= _options.MaxResidents)
                break;

            await memory.SetResidencyAsync(entry, MemoryResidency.Resident, cancellationToken).ConfigureAwait(false);
            await ledger.AppendAsync(
                "promote", $"{entry.Id} Active -> Resident: rate {stat!.UsefulRate:0.00} over {stat.Injections} injections",
                cancellationToken).ConfigureAwait(false);
            residentCount++;
            moves++;
        }

        return moves;
    }

    /// <summary>
    /// Episodic → semantic: ask the model — under the written policy — whether the
    /// accumulated episodes contain principles worth keeping, plus a dense summary that can
    /// stand in for them. The result is STAGED, never applied here.
    /// </summary>
    private async Task<string?> DistillAsync(
        IReadOnlyList<MemoryEntry> entries, CancellationToken cancellationToken)
    {
        var episodes = entries
            .Where(e => e is { Kind: MemoryKind.Episodic, Residency: MemoryResidency.Active })
            .OrderBy(e => e.CreatedAt)
            .Take(_options.MaxEpisodesPerPass)
            .ToArray();

        if (episodes.Length < _options.MinEpisodesToDistill)
            return null;

        var numbered = new StringBuilder();
        for (var i = 0; i < episodes.Length; i++)
            numbered.AppendLine($"[{i + 1}] {Clip(episodes[i].Content, 300)}");

        var request = new LlmRequest
        {
            System = policy,
            Messages =
            [
                Message.User(
                    "Below are archived conversation episodes, oldest first. Following the " +
                    "consolidation policy you were given:\n" +
                    "1. Extract principles that meet the policy's bar for generalizing (cite the " +
                    "episode numbers as evidence).\n" +
                    "2. Write ONE dense summary that can stand in for ALL these episodes.\n" +
                    "3. List the episode numbers the summary replaces (normally all of them).\n" +
                    "Return ONLY JSON: {\"facts\":[{\"scope\":\"personal|project|session\"," +
                    "\"fact\":\"...\",\"importance\":0-100,\"evidence\":\"episodes N,M\"}]," +
                    "\"summary\":\"...\",\"replaces\":[numbers]}\n\n" + numbered),
            ],
            Model = model,
            Purpose = LlmPurpose.Sleep,
            MaxTokens = 1500,
            Temperature = 0.1,
        };

        LlmResponse response;
        try
        {
            response = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // sleep is best-effort; try again next pass
        }

        var parsed = ParseProposal(response.Text);
        if (parsed is null)
            return null;

        var proposal = new ConsolidationProposal(
            Guid.NewGuid().ToString("N")[..12],
            DateTimeOffset.UtcNow,
            agentId,
            (parsed.Facts ?? [])
                .Where(f => !string.IsNullOrWhiteSpace(f.Fact))
                .Select(f => new DistilledFact(f.Scope, f.Fact, Math.Clamp(f.Importance, 0, 100) / 100.0, f.Evidence))
                .ToArray(),
            string.IsNullOrWhiteSpace(parsed.Summary) ? null : parsed.Summary,
            (parsed.Replaces ?? [])
                .Where(n => n >= 1 && n <= episodes.Length)
                .Distinct()
                .Select(n => episodes[n - 1].Id)
                .ToArray());

        if (proposal.Facts.Count == 0 && proposal.EpisodeSummary is null)
            return null;

        await pending.StageAsync(proposal, cancellationToken).ConfigureAwait(false);
        await ledger.AppendAsync(
            "stage", $"proposal {proposal.Id}: {proposal.Facts.Count} fact(s), " +
                     $"summary over {proposal.Replaces.Count} episode(s), agent {agentId}",
            cancellationToken).ConfigureAwait(false);
        return proposal.Id;
    }

    private static RawProposal? ParseProposal(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        try
        {
            return JsonSerializer.Deserialize<RawProposal>(text[start..(end + 1)], JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private sealed record RawFact(
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("fact")] string Fact,
        [property: JsonPropertyName("importance")] int Importance,
        [property: JsonPropertyName("evidence")] string Evidence);

    private sealed record RawProposal(
        [property: JsonPropertyName("facts")] List<RawFact>? Facts,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("replaces")] List<int>? Replaces);
}
