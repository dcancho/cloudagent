using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Surfaces;

/// <summary>
/// The hub between surfaces and agent sessions. It starts every surface, and for each inbound
/// message routes to the conversation that owns it — creating one on first contact via the
/// session factory — then ships the agent's replies back out to the originating surface.
///
/// One conversation per (surface, conversationId): each gets its own agent session, so many
/// chats run side by side, like a colleague juggling several threads.
/// </summary>
public sealed class ConversationRouter
{
    private readonly IReadOnlyList<ISurface> _surfaces;
    private readonly Func<string, string, AgentConversation> _sessionFactory;
    private readonly Func<InboundMessage, bool>? _authorize;

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    /// <param name="surfaces">The channels to listen on.</param>
    /// <param name="sessionFactory">Builds a conversation given (surface, conversationId).</param>
    /// <param name="authorize">Optional allowlist check; unauthorized messages get a refusal.</param>
    public ConversationRouter(
        IEnumerable<ISurface> surfaces,
        Func<string, string, AgentConversation> sessionFactory,
        Func<InboundMessage, bool>? authorize = null)
    {
        _surfaces = surfaces.ToArray();
        _sessionFactory = sessionFactory;
        _authorize = authorize;
    }

    /// <summary>Raised when a new conversation is opened (surface, conversationId).</summary>
    public event Action<string, string>? ConversationOpened;

    /// <summary>Start all surfaces and run until cancelled.</summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        // Each surface runs on its own thread: some adapters (the console reader) block
        // synchronously, and one must never stall the others from starting.
        var running = _surfaces.Select(surface => Task.Run(
            () => surface.StartAsync((message, ct) => OnInboundAsync(surface, message, ct), cancellationToken),
            cancellationToken));
        return Task.WhenAll(running);
    }

    private async Task OnInboundAsync(ISurface surface, InboundMessage message, CancellationToken cancellationToken)
    {
        if (_authorize is not null && !_authorize(message))
        {
            await surface
                .SendAsync(new OutboundMessage(message.ConversationId, "Sorry — you're not authorized to talk to me."), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var key = $"{message.Surface}:{message.ConversationId}";
        var session = _sessions.GetOrAdd(key, _ => OpenSession(surface, message, cancellationToken));
        await session.Conversation.PostAsync(message.Text, cancellationToken).ConfigureAwait(false);
    }

    private Session OpenSession(ISurface surface, InboundMessage message, CancellationToken cancellationToken)
    {
        var conversation = _sessionFactory(message.Surface, message.ConversationId);
        var conversationId = message.ConversationId;

        conversation.Responded += text =>
            _ = SendSafelyAsync(surface, new OutboundMessage(conversationId, text), cancellationToken);

        var runner = Task.Run(() => conversation.RunAsync(cancellationToken), cancellationToken);
        ConversationOpened?.Invoke(message.Surface, conversationId);
        return new Session(conversation, runner);
    }

    private static async Task SendSafelyAsync(ISurface surface, OutboundMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await surface.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A surface send failure shouldn't take down the conversation.
        }
    }

    private sealed record Session(AgentConversation Conversation, Task Runner);
}
