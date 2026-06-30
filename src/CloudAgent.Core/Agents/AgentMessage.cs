using System;

namespace CloudAgent.Core.Agents;

/// <summary>What an inter-agent message means.</summary>
public enum MessageKind
{
    /// <summary>A task assignment (parent → child).</summary>
    Task,

    /// <summary>A question expecting an answer (correlated by <see cref="AgentMessage.CorrelationId"/>).</summary>
    Question,

    /// <summary>An answer to a prior question.</summary>
    Answer,

    /// <summary>A progress update.</summary>
    Progress,

    /// <summary>A final result (child → parent).</summary>
    Result,

    /// <summary>An error report.</summary>
    Error,

    /// <summary>A plain message with no special semantics.</summary>
    Note,
}

/// <summary>
/// One message routed between agents through the <see cref="AgentBus"/>. Questions carry a
/// <see cref="CorrelationId"/> so the matching answer can be delivered back to the waiting caller.
/// </summary>
public sealed record AgentMessage(
    string From,
    string To,
    MessageKind Kind,
    string Content,
    string? CorrelationId = null)
{
    public DateTimeOffset SentAt { get; init; } = DateTimeOffset.UtcNow;
}
