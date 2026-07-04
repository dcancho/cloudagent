using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Memory;

/// <summary>
/// The policy layer over <see cref="IMemoryStore"/>. Owns its own embedder (the host decides
/// how cheap) and the agent's ambient metadata — context like agent/surface/conversation that
/// is known at write time and stamped onto every entry, so keyed lookup never has to
/// reconstruct it at read time.
/// </summary>
public sealed class MemoryManager(
    IMemoryStore store,
    IEmbeddingProvider embeddings,
    MemoryScope sessionScope = MemoryScope.Session,
    IReadOnlyDictionary<string, string>? ambientMetadata = null)
{
    public IMemoryStore Store => store;

    /// <summary>Persist a curated general fact. <paramref name="source"/> records its origin
    /// (e.g. "remember" for the explicit tool, "consolidation" for the learned pass);
    /// <paramref name="metadata"/> adds per-fact keys on top of the ambient ones.</summary>
    public async Task<MemoryEntry> RememberAsync(
        string content,
        MemoryScope scope,
        double importance = 0.5,
        string source = "remember",
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var embedding = await embeddings.EmbedAsync(content, cancellationToken).ConfigureAwait(false);
        var entry = new MemoryEntry(
            Guid.NewGuid().ToString("N"), scope, MemoryKind.Fact, content,
            embedding, importance, DateTimeOffset.UtcNow, source, [])
        {
            Metadata = Merge(metadata),
        };

        await store.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        return entry;
    }

    /// <summary>Recall the most relevant memories for a query across the given scopes.</summary>
    public async Task<IReadOnlyList<MemoryHit>> RecallAsync(
        string query,
        IReadOnlyCollection<MemoryScope> scopes,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var queryVector = await embeddings.EmbedAsync(query, cancellationToken).ConfigureAwait(false);
        return await store.RecallAsync(query, queryVector, scopes, topK, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Embed and archive conversation turns that are leaving the working window, as episodic
    /// memory in the session scope — so they remain recallable later.
    /// </summary>
    public async Task ArchiveAsync(
        IReadOnlyList<Message> messages, CancellationToken cancellationToken = default)
    {
        var rendered = messages
            .Select(Render)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        if (rendered.Length == 0)
            return;

        var vectors = await embeddings.EmbedAsync(rendered, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var metadata = Merge(null);

        for (var i = 0; i < rendered.Length; i++)
        {
            var entry = new MemoryEntry(
                Guid.NewGuid().ToString("N"), sessionScope, MemoryKind.Episodic, rendered[i],
                vectors[i], 0.3, now, "compaction", [])
            {
                Metadata = metadata,
            };
            await store.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Ambient metadata overlaid with per-write keys (per-write wins on conflict).</summary>
    private IReadOnlyDictionary<string, string> Merge(IReadOnlyDictionary<string, string>? metadata)
    {
        if (ambientMetadata is null or { Count: 0 })
            return metadata ?? System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;
        if (metadata is null or { Count: 0 })
            return ambientMetadata;

        var merged = new Dictionary<string, string>(ambientMetadata);
        foreach (var (key, value) in metadata)
            merged[key] = value;
        return merged;
    }

    private static string Render(Message message) => message.Role switch
    {
        Role.User => $"User: {message.Content}",
        Role.Assistant when message.ToolCalls.Count > 0 =>
            $"Assistant called {string.Join(", ", message.ToolCalls.Select(c => c.Name))}. {message.Content}".Trim(),
        Role.Assistant => $"Assistant: {message.Content}",
        Role.Tool => $"Tool {message.ToolName}: {message.Content}",
        _ => message.Content,
    };
}
