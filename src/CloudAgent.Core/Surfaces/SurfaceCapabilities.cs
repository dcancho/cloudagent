using System;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Surfaces;

/// <summary>
/// Someone is typing in a conversation. An expiring signal rather than a start and stop pair:
/// surfaces renew it while keys are pressed and it lapses on its own.
/// </summary>
/// <param name="Surface">The surface it came from.</param>
/// <param name="ConversationId">The conversation being typed in.</param>
/// <param name="SenderId">Who is typing.</param>
/// <param name="ExpiresAt">When the signal stops meaning anything unless renewed.</param>
public sealed record InboundTyping(string Surface, string ConversationId, string SenderId, DateTimeOffset ExpiresAt);

/// <summary>A surface that reports when people are typing.</summary>
public interface ITypingSource : ISurface
{
    event Action<InboundTyping>? TypingReceived;
}

/// <summary>A surface that can show the agent as typing in a conversation.</summary>
public interface ITypingIndicator : ISurface
{
    /// <summary>Show the agent as typing for a few seconds; call again to keep it showing.</summary>
    Task SendTypingAsync(string conversationId, CancellationToken cancellationToken = default);
}
