using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Memory;

/// <summary>
/// The policy layer over <see cref="IMemoryStore"/>. Owns its own embedder — defaulting to a
/// cheap local one — so high-volume episodic archival and recall don't run up API costs.
/// </summary>
public sealed class MemoryManager(
    IMemoryStore store,
    IEmbeddingProvider embeddings,
    MemoryScope sessionScope = MemoryScope.Session)
{
    public IMemoryStore Store => store;

    /// <summary>Persist a curated general fact (the explicit `remember`).</summary>
    public async Task<MemoryEntry> RememberAsync(
        string content,
        MemoryScope scope,
        double importance = 0.5,
        CancellationToken cancellationToken = default)
    {
        var embedding = await embeddings.EmbedAsync(content, cancellationToken).ConfigureAwait(false);
        var entry = new MemoryEntry(
            Guid.NewGuid().ToString("N"), scope, MemoryKind.Fact, content,
            embedding, importance, DateTimeOffset.UtcNow, "remember", []);

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
        return await store.RecallAsync(queryVector, scopes, topK, cancellationToken).ConfigureAwait(false);
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

        for (var i = 0; i < rendered.Length; i++)
        {
            var entry = new MemoryEntry(
                Guid.NewGuid().ToString("N"), sessionScope, MemoryKind.Episodic, rendered[i],
                vectors[i], 0.3, now, "compaction", []);
            await store.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        }
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
