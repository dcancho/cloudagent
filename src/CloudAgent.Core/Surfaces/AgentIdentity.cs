using System;
using System.Text;

namespace CloudAgent.Core.Surfaces;

/// <summary>A surface that knows how people mention the agent on it.</summary>
public interface IHandleSource : ISurface
{
    /// <summary>How people address the agent here, such as <c>@CloudAgent</c>; null until the surface has connected.</summary>
    string? Handle { get; }
}

/// <summary>What the agent is told about itself and where it is talking, in the prompt of each conversation.</summary>
public static class AgentIdentity
{
    public const string DefaultName = "CloudAgent";

    /// <summary>The agent's name: the one configured, else its handle on the surface without the <c>@</c>, else the default.</summary>
    public static string NameOf(string? configured, string? handle) =>
        !string.IsNullOrWhiteSpace(configured) ? configured.Trim()
        : !string.IsNullOrWhiteSpace(handle) ? handle.TrimStart('@')
        : DefaultName;

    /// <summary>The sentences to append to the system prompt of a conversation.</summary>
    /// <param name="name">The agent's name.</param>
    /// <param name="handle">How people mention it on the surface, if known.</param>
    /// <param name="surface">The surface the conversation is on.</param>
    /// <param name="kind">Whether it is one person's direct line or a group.</param>
    /// <param name="person">Who the direct conversation is with, if known.</param>
    public static string Describe(string name, string? handle, string surface, ConversationKind kind, string? person)
    {
        var text = new StringBuilder($" Your name is {name}.");

        if (kind == ConversationKind.Direct)
        {
            text.Append($" This is a direct conversation on {surface}")
                .Append(string.IsNullOrWhiteSpace(person) ? "." : $" with {person}.")
                .Append(" Only that person can read it.");
            return text.ToString();
        }

        text.Append($" This is a group conversation on {surface}, so several people read everything you write.");
        if (!string.IsNullOrWhiteSpace(handle))
            text.Append($" People mention you there as {handle}.");
        text.Append(
            " Each line starts with who wrote it; a line that starts with another name is not yours." +
            " You are only sent messages that address you or come from someone you are already talking with." +
            " Lines marked [overheard] are earlier messages in the group that were not addressed to you:" +
            " use them as context, do not answer them or mention that you saw them unless it helps." +
            " Answer the person who addressed you, by name when several people are talking." +
            " Do not share what one person told you in a direct conversation.");
        return text.ToString();
    }
}
