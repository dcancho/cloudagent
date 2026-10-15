using System;

namespace CloudAgent.Core.Surfaces;

/// <summary>A conversation as a list shows it: its id on the surface, a title, and when it last changed.</summary>
public sealed record ConversationSummary(string Id, string Title, DateTimeOffset UpdatedAt);
