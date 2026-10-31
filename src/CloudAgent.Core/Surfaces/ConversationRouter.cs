using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
    private readonly Func<InboundMessage, AgentConversation> _sessionFactory;
    private readonly Func<InboundMessage, bool>? _authorize;
    private readonly Func<InboundMessage, string?> _speakerOf;
    private readonly ParticipationGate? _participation;
    private readonly TurnTaker _turns;
    private readonly InboxStore? _inbox;
    private readonly RedeliveryOptions _redelivery;
    private readonly TimeProvider _time;
    private CancellationToken _runToken;

    private readonly ConcurrentDictionary<InboundMessage, string> _entryIds = new(ReferenceEqualityComparer.Instance);
    private readonly object _idleGate = new();
    private readonly Dictionary<string, string> _inFlight = new();
    private TaskCompletionSource? _idle;
    private int _sending;
    private volatile bool _draining;

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    /// <param name="surfaces">The channels to listen on.</param>
    /// <param name="sessionFactory">Builds the conversation that a first message opens.</param>
    /// <param name="authorize">Optional allowlist check. Unauthorized messages get a refusal in a direct
    /// conversation and are ignored in a group, where a refusal would only be noise.</param>
    /// <param name="turnTaking">End-of-turn detection settings; defaults apply when null.</param>
    /// <param name="speakerOf">Names the sender of a message, such as the person it resolves to, so
    /// a group's messages can be attributed. Senders it returns null for go by their display name.</param>
    /// <param name="participation">When set, the agent answers in a group only when addressed or already
    /// in conversation with the sender; other messages are kept as context. Without it every group
    /// message gets a turn.</param>
    /// <param name="inbox">When set, every accepted message is written down before the agent sees it and
    /// struck off when its turn ends; what a restart left behind is redelivered once the surfaces are up.
    /// The console is never written down.</param>
    /// <param name="redelivery">Limits and notices for redelivery; defaults apply when null.</param>
    /// <param name="time">The clock used to age inbox entries.</param>
    public ConversationRouter(
        IEnumerable<ISurface> surfaces,
        Func<InboundMessage, AgentConversation> sessionFactory,
        Func<InboundMessage, bool>? authorize = null,
        TurnTakingOptions? turnTaking = null,
        Func<InboundMessage, string?>? speakerOf = null,
        ParticipationGate? participation = null,
        InboxStore? inbox = null,
        RedeliveryOptions? redelivery = null,
        TimeProvider? time = null)
    {
        _inbox = inbox;
        _redelivery = redelivery ?? new RedeliveryOptions();
        _time = time ?? TimeProvider.System;
        _surfaces = surfaces.ToDictionary(s => s.Name);
        _sessionFactory = sessionFactory;
        _authorize = authorize;
        _speakerOf = speakerOf ?? (_ => null);
        _participation = participation;
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
        var interrupted = _inbox?.Pending() ?? [];

        foreach (var source in _surfaces.Values.OfType<ITypingSource>())
            source.TypingReceived += _turns.Typing;

        // Each surface runs on its own thread: some adapters (the console reader) block
        // synchronously, and one must never stall the others from starting.
        var running = _surfaces.Values.Select(surface => Task.Run(
            () => surface.StartAsync((message, ct) => OnInboundAsync(surface, message, ct), cancellationToken),
            cancellationToken)).ToList();
        _ = Task.Run(() => RedeliverAsync(interrupted, cancellationToken), cancellationToken);
        return Task.WhenAll(running);
    }

    /// <summary>
    /// Stop taking new work and let what is running finish: waiting bursts are handed over at once, and
    /// the call returns when every accepted message has had its turn and its reply is sent, or after
    /// <paramref name="timeout"/>. Messages that arrive meanwhile are only written down. Whatever
    /// is left unfinished stays in the inbox for the next start.
    /// </summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        _draining = true;
        _turns.Flush();

        using var limit = new CancellationTokenSource(timeout);
        try
        {
            await WaitUntilIdleAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"[router] drain gave up with {UnfinishedCount} message(s) unfinished; they stay in the inbox");
        }
    }

    /// <summary>
    /// End a conversation's session, if one is open: drop its waiting messages, stop its loop, wait
    /// for the loop to finish, and release what it holds. A later message opens a new session.
    /// </summary>
    public async Task CloseAsync(string surface, string conversationId)
    {
        _turns.Discard(surface, conversationId);
        _inbox?.Clear(surface, conversationId);
        Release(ids: null, session: $"{surface}:{conversationId}");
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
            if (message.Kind == ConversationKind.Group)
                return;

            await surface
                .SendAsync(new OutboundMessage(message.ConversationId, "Sorry, you're not authorized to talk to me."), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var admitted = _participation is null ? message : _participation.Admit(message);
        if (admitted is null)
            return;

        if (_inbox is not null && !Ephemeral(admitted.Surface))
        {
            try
            {
                var id = _inbox.Add(admitted);
                _entryIds[admitted] = id;
                if (!_draining)
                    Track(id, $"{admitted.Surface}:{admitted.ConversationId}");
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"[router] could not write a message to the inbox: {ex.Message}");
            }
        }

        if (!_draining)
            _turns.Add(admitted);
    }

    private static bool Ephemeral(string surface) => surface == "cli";

    private async Task RedeliverAsync(IReadOnlyList<InboxEntry> interrupted, CancellationToken cancellationToken)
    {
        if (_inbox is null)
            return;

        try
        {
            var conversations = interrupted
                .GroupBy(e => (e.Message.Surface, e.Message.ConversationId))
                .Select(g => RedeliverConversationAsync(g.Key.Surface, g.Key.ConversationId, g.ToList(), cancellationToken));
            await Task.WhenAll(conversations).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[router] could not redeliver interrupted messages: {ex.Message}");
        }
    }

    private async Task RedeliverConversationAsync(
        string surfaceName, string conversationId, List<InboxEntry> entries, CancellationToken cancellationToken)
    {
        if (!_surfaces.TryGetValue(surfaceName, out var surface))
            return;

        try
        {
            if (surface is IReadinessSource readiness)
                await readiness.WaitUntilReadyAsync(conversationId, cancellationToken)
                    .WaitAsync(_redelivery.ReadyTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"[router] {surfaceName}:{conversationId} never became ready; its interrupted messages stay in the inbox");
            return;
        }

        if (_draining)
            return;

        var now = _time.GetUtcNow();
        var tooOld = entries.Where(e => now - e.PersistedAt > _redelivery.MaxAge).ToList();
        var worn = entries.Where(e => e.Attempts >= _redelivery.MaxRedeliveries).Except(tooOld).ToList();
        var revoked = _authorize is null
            ? []
            : entries.Where(e => !_authorize(e.Message)).Except(tooOld).Except(worn).ToList();
        var dropped = tooOld.Concat(worn).Concat(revoked).ToList();
        var fresh = entries.Except(dropped).ToList();

        _inbox!.Complete(dropped.Select(e => e.Id));
        foreach (var entry in worn)
            Console.Error.WriteLine($"[router] gave up on a message from {surfaceName}:{conversationId} after {entry.Attempts} redelivery attempt(s)");

        if (fresh.Count == 0)
        {
            if (tooOld.Count > 0)
                await SendSafelyAsync(surface, new OutboundMessage(conversationId, _redelivery.StaleNotice), cancellationToken).ConfigureAwait(false);
            return;
        }

        var ids = fresh.Select(e => e.Id).ToList();
        _inbox.RecordAttempt(ids);
        foreach (var id in ids)
            Track(id, $"{surfaceName}:{conversationId}");
        foreach (var entry in fresh)
            _entryIds[entry.Message] = entry.Id;

        await SendSafelyAsync(surface, new OutboundMessage(conversationId, _redelivery.Notice), cancellationToken).ConfigureAwait(false);
        await DeliverAsync(new Burst(surfaceName, conversationId, fresh.Select(e => e.Message).ToList())).ConfigureAwait(false);
    }

    private int UnfinishedCount
    {
        get
        {
            lock (_idleGate)
                return _inFlight.Count;
        }
    }

    private void Track(string id, string session)
    {
        lock (_idleGate)
            _inFlight[id] = session;
    }

    private void Release(IEnumerable<string>? ids, string? session = null)
    {
        lock (_idleGate)
        {
            foreach (var id in ids ?? _inFlight.Where(p => p.Value == session).Select(p => p.Key).ToList())
                _inFlight.Remove(id);
            SignalIfIdle();
        }
    }

    private void BeginSend()
    {
        lock (_idleGate)
            _sending++;
    }

    private void EndSend()
    {
        lock (_idleGate)
        {
            _sending--;
            SignalIfIdle();
        }
    }

    private void SignalIfIdle()
    {
        if (_inFlight.Count == 0 && _sending == 0)
        {
            _idle?.TrySetResult();
            _idle = null;
        }
    }

    private async Task WaitUntilIdleAsync(CancellationToken cancellationToken)
    {
        Task wait;
        lock (_idleGate)
        {
            if (_inFlight.Count == 0 && _sending == 0)
                return;
            wait = (_idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Settle(string receipt)
    {
        var ids = receipt.Split(',');
        try
        {
            _inbox?.Complete(ids);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[router] could not strike a message off the inbox: {ex.Message}");
        }
        Release(ids);
    }

    private string? ReceiptOf(Burst burst)
    {
        var ids = burst.Messages.Select(m => _entryIds.TryRemove(m, out var id) ? id : null).OfType<string>().ToList();
        return ids.Count == 0 ? null : string.Join(',', ids);
    }

    private async Task DeliverAsync(Burst burst)
    {
        var surface = _surfaces[burst.Surface];
        var key = $"{burst.Surface}:{burst.ConversationId}";
        var session = _sessions.GetOrAdd(key, _ => OpenSession(surface, burst.Messages[0]));

        if (surface is ITypingIndicator indicator)
            session.StartTyping(indicator, _runToken);

        var text = burst.TextAttributed(_speakerOf);
        if (ReceiptOf(burst) is { } receipt)
            await session.Conversation.PostAsync(text, receipt, _runToken).ConfigureAwait(false);
        else
            await session.Conversation.PostAsync(text, _runToken).ConfigureAwait(false);
    }

    private Session OpenSession(ISurface surface, InboundMessage first)
    {
        var conversationId = first.ConversationId;
        var conversation = _sessionFactory(first);
        var session = new Session(conversation, conversationId, CancellationTokenSource.CreateLinkedTokenSource(_runToken));

        conversation.Responded += text =>
        {
            session.StopTyping();
            _turns.MarkAnswered(surface.Name, conversationId);
            _participation?.Answered(surface.Name, conversationId);
            BeginSend();
            _ = SendTrackedAsync(surface, new OutboundMessage(conversationId, text), _runToken);
        };
        conversation.Settled += Settle;

        var token = session.Cancellation.Token;
        if (surface is IActivityIndicator activity)
            conversation.Acted += a => _ = SendActivitySafelyAsync(activity, conversationId, a, _runToken);

        session.Runner = Task.Run(() => conversation.RunAsync(token), token);
        ConversationOpened?.Invoke(surface.Name, conversationId);
        return session;
    }

    private async Task SendTrackedAsync(ISurface surface, OutboundMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await SendSafelyAsync(surface, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndSend();
        }
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

    private static async Task SendActivitySafelyAsync(
        IActivityIndicator surface, string conversationId, ToolActivity activity, CancellationToken cancellationToken)
    {
        try
        {
            await surface.SendActivityAsync(conversationId, activity, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Activity is informational; a failure to show it must not affect the turn.
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
