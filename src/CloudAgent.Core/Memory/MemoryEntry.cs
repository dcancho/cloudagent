using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace CloudAgent.Core.Memory;

/// <summary>
/// Where a memory belongs, in the coworker model:
/// <list type="bullet">
/// <item><see cref="Personal"/> — about the user and the agent's own working style; spans every project.</item>
/// <item><see cref="Project"/> — about this workspace: architecture, decisions, constraints.</item>
/// <item><see cref="Session"/> — about the current run; short-lived, may be promoted on consolidation.</item>
/// </list>
/// </summary>
public enum MemoryScope
{
    Personal,
    Project,
    Session,
}

/// <summary>What kind of memory this is.</summary>
public enum MemoryKind
{
    /// <summary>A curated/learned general fact (from `remember` or consolidation).</summary>
    Fact,

    /// <summary>An archived slice of past conversation, kept recallable after it left the window.</summary>
    Episodic,
}

/// <summary>
/// Which residence stratum a memory lives in — a generational ladder driven by measured
/// utility (the ledger), not by guesses:
/// <list type="bullet">
/// <item><see cref="Resident"/> — earns a permanent place in the system prompt (high hit rate).</item>
/// <item><see cref="Active"/> — participates in per-turn recall (the default).</item>
/// <item><see cref="Cold"/> — parked on disk; excluded from recall, retrievable only deliberately.
/// Demotion here is this harness's "forgetting": reversible, never destructive.</item>
/// </list>
/// </summary>
public enum MemoryResidency
{
    Resident,
    Active,
    Cold,
}

/// <summary>
/// One durable memory: a piece of content with its embedding and metadata. Stored as a
/// record so memory files are plain, inspectable data.
/// </summary>
public sealed record MemoryEntry(
    string Id,
    MemoryScope Scope,
    MemoryKind Kind,
    string Content,
    float[] Embedding,
    double Importance,
    DateTimeOffset CreatedAt,
    string? Source,
    IReadOnlyList<string> Links)
{
    /// <summary>
    /// Structured context captured at write time — keys like "agent", "surface",
    /// "conversation", "person". Enriching on write is cheap (it happens once, and the
    /// writer knows the context); it makes keyed lookup and filtered recall possible later
    /// without re-deriving anything at read time.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Residence stratum; moved between strata by the sleep job, never by hand.</summary>
    public MemoryResidency Residency { get; init; } = MemoryResidency.Active;
}

/// <summary>A recalled memory and how closely it matched.</summary>
/// <param name="Entry">The matched memory.</param>
/// <param name="Score">Cosine similarity in [-1, 1].</param>
public sealed record MemoryHit(MemoryEntry Entry, double Score);
