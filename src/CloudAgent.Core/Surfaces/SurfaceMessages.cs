using System;

namespace CloudAgent.Core.Surfaces;

/// <summary>
/// A message arriving from the outside world through a surface. <see cref="ConversationId"/>
/// is the routing key (a chat/thread/channel) that maps to one agent session.
/// </summary>
/// <param name="Surface">The surface it came from (e.g. "telegram", "cli").</param>
/// <param name="ConversationId">The chat/thread/channel id, unique within the surface.</param>
/// <param name="Kind">Whether the conversation is one person's direct line or shared by a group.</param>
/// <param name="SenderId">Who sent it (for identity / authorization).</param>
/// <param name="SenderDisplay">A human-readable sender name, if known.</param>
/// <param name="Text">The message text.</param>
/// <param name="ReceivedAt">When it arrived.</param>
public sealed record InboundMessage(
    string Surface,
    string ConversationId,
    ConversationKind Kind,
    string SenderId,
    string? SenderDisplay,
    string Text,
    DateTimeOffset ReceivedAt);

/// <summary>Who a conversation is with: decided by the channel, not by how many people are in it now.</summary>
public enum ConversationKind
{
    /// <summary>A one-to-one line with a single person (a DM, the web chat, the console).</summary>
    Direct,

    /// <summary>A channel, group or thread that other people can read and join.</summary>
    Group,
}

/// <summary>A message the agent is sending out to a conversation on a surface.</summary>
/// <param name="ConversationId">Where to deliver it.</param>
/// <param name="Text">The message text.</param>
public sealed record OutboundMessage(string ConversationId, string Text);
