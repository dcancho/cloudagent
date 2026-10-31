using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Memory;

/// <summary>A fact the sleep job proposes to learn, with the evidence that justifies it.</summary>
public sealed record DistilledFact(string Scope, string Fact, double Importance, string Evidence);

/// <summary>
/// What one sleep pass proposes: facts distilled from episodes, a dense summary that
/// replaces those episodes, and the ids of the episodes it condenses.
/// </summary>
public sealed record ConsolidationProposal(
    string Id,
    DateTimeOffset At,
    string AgentId,
    IReadOnlyList<DistilledFact> Facts,
    string? EpisodeSummary,
    IReadOnlyList<string> Replaces);

/// <summary>
/// The staging area for semantic consolidation. The sleep job never writes distilled facts
/// directly — it stages a proposal here, and a human (or an explicitly trusted agent turn)
/// applies or discards it. Mechanical strata moves don't pass through here; only writes that
/// create new "knowledge" do, because those are the ones worth auditing before they exist.
/// </summary>
public sealed class PendingConsolidations(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Stage a proposal; it sits on disk until applied or discarded.</summary>
    public async Task StageAsync(ConsolidationProposal proposal, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{proposal.Id}.json");
        await File.WriteAllTextAsync(
            path, JsonSerializer.Serialize(proposal, JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<ConsolidationProposal> List()
    {
        if (!Directory.Exists(directory))
            return [];

        return Directory.EnumerateFiles(directory, "*.json")
            .Select(Load)
            .Where(p => p is not null)
            .Select(p => p!)
            .OrderBy(p => p.At)
            .ToArray();
    }

    public ConsolidationProposal? Get(string id) =>
        List().FirstOrDefault(p => p.Id == id);

    public void Remove(string id)
    {
        var path = Path.Combine(directory, $"{id}.json");
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>An operator's decision on a staged proposal, left for the host to carry out.</summary>
    public enum Decision
    {
        Approve,
        Discard,
    }

    /// <summary>
    /// Record a decision on a staged proposal without applying it. The host that owns the memory and the
    /// ledger carries it out, so a second process never writes either.
    /// </summary>
    public void Decide(string id, Decision decision)
    {
        if (Get(id) is null)
            throw new InvalidOperationException($"No pending proposal '{id}'.");

        File.WriteAllText(Path.Combine(directory, $"{id}.decision"), decision.ToString().ToLowerInvariant());
    }

    /// <summary>The decisions waiting to be carried out, by proposal id.</summary>
    public IReadOnlyDictionary<string, Decision> Decisions()
    {
        var decisions = new Dictionary<string, Decision>();
        if (!Directory.Exists(directory))
            return decisions;

        foreach (var path in Directory.EnumerateFiles(directory, "*.decision"))
        {
            if (Enum.TryParse<Decision>(File.ReadAllText(path).Trim(), ignoreCase: true, out var decision))
                decisions[Path.GetFileNameWithoutExtension(path)] = decision;
        }
        return decisions;
    }

    /// <summary>Forget a decision once it has been carried out.</summary>
    public void ClearDecision(string id)
    {
        var path = Path.Combine(directory, $"{id}.decision");
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>
    /// Apply a staged proposal: write its facts (source "distillation"), store the dense
    /// summary, and demote the episodes it replaces to Cold — condensed, never destroyed.
    /// </summary>
    public async Task<string> ApplyAsync(
        ConsolidationProposal proposal,
        MemoryManager memory,
        ConsolidationLedger ledger,
        CancellationToken cancellationToken = default)
    {
        var written = 0;
        foreach (var fact in proposal.Facts)
        {
            if (string.IsNullOrWhiteSpace(fact.Fact))
                continue;
            if (!Enum.TryParse<MemoryScope>(fact.Scope, ignoreCase: true, out var scope))
                scope = MemoryScope.Project;
            if (!memory.Holds(scope))
                continue;

            await memory.RememberAsync(
                fact.Fact, scope, Math.Clamp(fact.Importance, 0, 1), source: "distillation",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            written++;
        }

        if (!string.IsNullOrWhiteSpace(proposal.EpisodeSummary))
            await memory.ArchiveSummaryAsync(proposal.EpisodeSummary, cancellationToken).ConfigureAwait(false);

        var demoted = 0;
        if (proposal.Replaces.Count > 0)
        {
            var all = await memory.ListAsync(
                [MemoryScope.Personal, MemoryScope.Project, MemoryScope.Session], cancellationToken)
                .ConfigureAwait(false);
            var byId = all.ToDictionary(e => e.Id);
            foreach (var id in proposal.Replaces)
            {
                if (byId.TryGetValue(id, out var episode) && episode.Residency != MemoryResidency.Cold)
                {
                    await memory.SetResidencyAsync(episode, MemoryResidency.Cold, cancellationToken)
                        .ConfigureAwait(false);
                    demoted++;
                }
            }
        }

        Remove(proposal.Id);
        var outcome = $"proposal {proposal.Id}: {written} fact(s) written, " +
                      $"{(proposal.EpisodeSummary is null ? 0 : 1)} summary, {demoted} episode(s) demoted to Cold";
        await ledger.AppendAsync("apply", outcome, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    private static ConsolidationProposal? Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ConsolidationProposal>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
