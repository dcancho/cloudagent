using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Surfaces;

/// <summary>
/// The hub between surfaces and agent sessions. It starts every surface, groups each
/// conversation's inbound messages into bursts (see <see cref="TurnTaker"/>), and hands each burst
/// to the conversation that owns it, creating one on first contact via the session factory. Replies
/// go back out to the originating surface, and surfaces that can show typing do so while the agent
/// works on a turn.
///
/// One conversation per (surface, conversationId): each gets its own agent session, so many
/// chats run side by side, like a colleague juggling several threads.
/// </summary>
public sealed class ConversationRouter : IDisposable
{
    private static readonly TimeSpan TypingRenewal = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TypingLimit = TimeSpan.FromSeconds(90);

    private readonly IReadOnlyDictionary<string, ISurface> _surfaces;
    private readonly Func<string, string, AgentConversation> _sessionFactory;
    private readonly Func<InboundMessage, bool>? _authorize;
    private readonly TurnTaker _turns;
    private CancellationToken _runToken;

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    /// <param name="surfaces">The channels to listen on.</param>
    /// <param name="sessionFactory">Builds a conversation given (surface, conversationId).</param>
    /// <param name="authorize">Optional allowlist check; unauthorized messages get a refusal.</param>
    /// <param name="turnTaking">End-of-turn detection settings; defaults apply when null.</param>
    public ConversationRouter(
        IEnumerable<ISurface> surfaces,
        Func<string, string, AgentConversation> sessionFactory,
        Func<InboundMessage, bool>? authorize = null,
        TurnTakingOptions? turnTaking = null)
    {
        _surfaces = surfaces.ToDictionary(s => s.Name);
        _sessionFactory = sessionFactory;
        _authorize = authorize;
        _turns = new TurnTaker(DeliverAsync, turnTaking);
        _turns.DeliveryFailed += ex => Console.Error.WriteLine($"[router] could not deliver a turn: {ex.Message}");
    }

    /// <summary>Raised when a new conversation is opened (surface, conversationId).</summary>
    public event Action<string, string>? ConversationOpened;

    /// <summary>The end-of-turn component, exposed for diagnostics.</summary>
    public TurnTaker Turns => _turns;

    /// <summary>Start all surfaces and run until cancelled.</summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        _runToken = cancellationToken;

        foreach (var source in _surfaces.Values.OfType<ITypingSource>())
            source.TypingReceived += _turns.Typing;

        // Each surface runs on its own thread: some adapters (the console reader) block
        // synchronously, and one must never stall the others from starting.
        var running = _surfaces.Values.Select(surface => Task.Run(
            () => surface.StartAsync((message, ct) => OnInboundAsync(surface, message, ct), cancellationToken),
            cancellationToken));
        return Task.WhenAll(running);
    }

    /// <summary>
    /// End a conversation's session, if one is open: drop its waiting messages, stop its loop, wait
    /// for the loop to finish, and release what it holds. A later message opens a new session.
    /// </summary>
    public async Task CloseAsync(string surface, string conversationId)
    {
        _turns.Discard(surface, conversationId);
        if (!_sessions.TryRemove($"{surface}:{conversationId}", out var session))
            return;

        session.StopTyping();
        await session.Cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            if (session.Runner is not null)
                await session.Runner.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            session.Cancellation.Dispose();
            session.Conversation.Dispose();
        }
    }

    public void Dispose() => _turns.Dispose();

    private async Task OnInboundAsync(ISurface surface, InboundMessage message, CancellationToken cancellationToken)
    {
        if (_authorize is not null && !_authorize(message))
        {
            await surface
                .SendAsync(new OutboundMessage(message.ConversationId, "Sorry, you're not authorized to talk to me."), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _turns.Add(message);
    }

    private async Task DeliverAsync(Burst burst)
    {
        var surface = _surfaces[burst.Surface];
        var key = $"{burst.Surface}:{burst.ConversationId}";
        var session = _sessions.GetOrAdd(key, _ => OpenSession(surface, burst.ConversationId));

        if (surface is ITypingIndicator indicator)
            session.StartTyping(indicator, _runToken);

        await session.Conversation.PostAsync(burst.Text, _runToken).ConfigureAwait(false);
    }

    private Session OpenSession(ISurface surface, string conversationId)
    {
        var conversation = _sessionFactory(surface.Name, conversationId);
        var session = new Session(conversation, conversationId, CancellationTokenSource.CreateLinkedTokenSource(_runToken));

        conversation.Responded += text =>
        {
            session.StopTyping();
            _turns.MarkAnswered(surface.Name, conversationId);
            _ = SendSafelyAsync(surface, new OutboundMessage(conversationId, text), _runToken);
        };

        var token = session.Cancellation.Token;
        session.Runner = Task.Run(() => conversation.RunAsync(token), token);
        ConversationOpened?.Invoke(surface.Name, conversationId);
        return session;
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

    private sealed class Session(AgentConversation conversation, string conversationId, CancellationTokenSource cancellation)
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _typing;

        public AgentConversation Conversation { get; } = conversation;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? Runner { get; set; }

        public void StartTyping(ITypingIndicator indicator, CancellationToken runToken)
        {
            CancellationTokenSource typing;
            lock (_gate)
            {
                _typing?.Cancel();
                _typing = typing = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                typing.CancelAfter(TypingLimit);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!typing.IsCancellationRequested)
                    {
                        await indicator.SendTypingAsync(conversationId, typing.Token).ConfigureAwait(false);
                        await Task.Delay(TypingRenewal, typing.Token).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Typing is cosmetic; a failure or cancellation just stops it.
                }
            });
        }

        public void StopTyping()
        {
            lock (_gate)
            {
                _typing?.Cancel();
                _typing = null;
            }
        }
    }
}
