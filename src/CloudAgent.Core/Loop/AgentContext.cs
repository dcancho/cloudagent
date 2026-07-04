using System.Collections.Generic;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Loop;

/// <summary>
/// The agent's working context: the running conversation/history the loop feeds to the
/// provider each iteration. This is the natural home for future memory management
/// (summarisation, trimming, retrieval) — the loop only ever talks to this.
/// </summary>
public sealed class AgentContext
{
    private readonly List<Message> _messages = [];

    public IReadOnlyList<Message> Messages => _messages;

    /// <summary>
    /// Condensed summary of earlier turns that have been compacted out of <see cref="Messages"/>.
    /// Carried into the request out-of-band (alongside the system prompt) so it survives
    /// trimming without breaking message role alternation.
    /// </summary>
    public string? RollingSummary { get; set; }

    /// <summary>
    /// Structured snapshot of where the work stands (active task verbatim, verified,
    /// discarded, decisions, blockers, remaining). Carried out-of-band like the summary,
    /// but updated field-by-field on compaction rather than re-summarized.
    /// </summary>
    public OperationalState State { get; set; } = new();

    public void Add(Message message) => _messages.Add(message);

    public void AddUser(string content) => Add(Message.User(content));

    public void AddAssistant(string content) => Add(Message.Assistant(content));

    /// <summary>True when there is nothing yet for the provider to respond to.</summary>
    public bool IsEmpty => _messages.Count == 0;

    /// <summary>The most recent message, or null if the context is empty.</summary>
    public Message? Last => _messages.Count == 0 ? null : _messages[^1];

    /// <summary>
    /// Remove the oldest messages, keeping roughly the most recent <paramref name="keepRecent"/>.
    /// The kept window is snapped forward to start on a user turn, which upholds two
    /// invariants at once: a tool call is never separated from its result, and the latest
    /// user message is never evicted into the summary — it always survives as a real,
    /// anchoring message. Returns the evicted messages in order (empty if no clean cut).
    /// </summary>
    public IReadOnlyList<Message> EvictOldest(int keepRecent)
    {
        if (keepRecent < 0 || _messages.Count <= keepRecent)
            return [];

        var cut = _messages.Count - keepRecent;
        while (cut < _messages.Count && _messages[cut].Role != Role.User)
            cut++;

        if (cut <= 0 || cut >= _messages.Count)
            return [];

        var evicted = _messages.GetRange(0, cut);
        _messages.RemoveRange(0, cut);
        return evicted;
    }
}
