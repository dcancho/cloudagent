using System;

namespace CloudAgent.Core.Todos;

public enum TodoStatus
{
    Pending,
    Done,
}

/// <summary>
/// One entry on the agent's taskboard. <see cref="Id"/> is a small, stable number so the
/// agent (and a human) can refer to a task by it ("complete 3").
/// </summary>
public sealed class TodoItem
{
    public required int Id { get; init; }

    public required string Title { get; set; }

    public TodoStatus Status { get; set; } = TodoStatus.Pending;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }
}
