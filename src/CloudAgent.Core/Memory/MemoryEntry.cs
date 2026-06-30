using System;
using System.Collections.Generic;

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
    IReadOnlyList<string> Links);

/// <summary>A recalled memory and how closely it matched.</summary>
/// <param name="Entry">The matched memory.</param>
/// <param name="Score">Cosine similarity in [-1, 1].</param>
public sealed record MemoryHit(MemoryEntry Entry, double Score);
