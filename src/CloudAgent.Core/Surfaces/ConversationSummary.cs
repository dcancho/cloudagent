using System;

namespace CloudAgent.Core.Surfaces;

/// <summary>A conversation as a list shows it: its id on the surface, a title, and when it last changed.</summary>
public sealed record ConversationSummary(string Id, string Title, DateTimeOffset UpdatedAt);

/// <summary>One entry of a conversation as people saw it: a message, or a tool call the agent made.</summary>
/// <param name="Role"><c>user</c>, <c>assistant</c>, or <c>activity</c>.</param>
/// <param name="Text">The message; empty for activity.</param>
/// <param name="Activity">The tool call, for activity.</param>
public sealed record ConversationEntry(string Role, string Text, ToolActivity? Activity = null);
