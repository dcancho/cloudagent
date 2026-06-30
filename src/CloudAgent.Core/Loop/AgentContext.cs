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

    public void Add(Message message) => _messages.Add(message);

    public void AddUser(string content) => Add(Message.User(content));

    public void AddAssistant(string content) => Add(Message.Assistant(content));

    /// <summary>True when there is nothing yet for the provider to respond to.</summary>
    public bool IsEmpty => _messages.Count == 0;

    /// <summary>The most recent message, or null if the context is empty.</summary>
    public Message? Last => _messages.Count == 0 ? null : _messages[^1];
}
