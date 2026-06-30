using System.Collections.Generic;

namespace CloudAgent.Core.Abstractions;

/// <summary>
/// Who authored a message in the conversation.
/// </summary>
public enum Role
{
    /// <summary>Steering/persona instructions the agent operates under.</summary>
    System,

    /// <summary>Input from the outside world (a human, a scheduler, an observation).</summary>
    User,

    /// <summary>The agent's own output.</summary>
    Assistant,

    /// <summary>The result of a tool the agent invoked, fed back to the model.</summary>
    Tool,
}

/// <summary>
/// A single turn in the agent's working context. A plain text turn for user/assistant
/// messages; an assistant turn may also carry <see cref="ToolCalls"/> it requested, and a
/// <see cref="Role.Tool"/> turn carries a tool's result tagged with the call it answers.
/// </summary>
/// <param name="Role">Who produced this message.</param>
/// <param name="Content">The textual content of the message.</param>
public sealed record Message(Role Role, string Content)
{
    /// <summary>Tool calls requested in this (assistant) message, if any.</summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>For a <see cref="Role.Tool"/> message: the id of the call it answers.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>For a <see cref="Role.Tool"/> message: the name of the tool that ran.</summary>
    public string? ToolName { get; init; }

    public static Message System(string content) => new(Role.System, content);
    public static Message User(string content) => new(Role.User, content);
    public static Message Assistant(string content) => new(Role.Assistant, content);

    /// <summary>An assistant message that requested one or more tools.</summary>
    public static Message Assistant(string content, IReadOnlyList<ToolCall> toolCalls) =>
        new(Role.Assistant, content) { ToolCalls = toolCalls };

    /// <summary>A tool-result message answering a specific tool call.</summary>
    public static Message ToolResult(string callId, string toolName, string content) =>
        new(Role.Tool, content) { ToolCallId = callId, ToolName = toolName };
}
