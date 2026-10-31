using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Surfaces;

/// <summary>Messages from one conversation that together make up one turn.</summary>
public sealed record Burst(string Surface, string ConversationId, IReadOnlyList<InboundMessage> Messages)
{
    /// <summary>The burst as the agent reads it: the messages in order, one per line.</summary>
    public string Text => string.Join('\n', Messages.Select(m => m.Text));

    /// <summary>
    /// The burst as the agent reads it when it must tell speakers apart: in a group every message
    /// is prefixed <c>name: </c>, using <paramref name="nameOf"/> when it knows the sender and the
    /// sender's display name or id otherwise. A direct conversation reads as plain <see cref="Text"/>.
    /// </summary>
    public string TextAttributed(Func<InboundMessage, string?> nameOf) =>
        string.Join('\n', Messages.Select(m => m.Kind == ConversationKind.Group
            ? $"{nameOf(m) ?? (string.IsNullOrWhiteSpace(m.SenderDisplay) ? m.SenderId : m.SenderDisplay)}: {m.Text}"
            : m.Text));
}

/// <summary>Knobs for end-of-turn detection.</summary>
public sealed class TurnTakingOptions
{
    /// <summary>Wait used before a person's rhythm has been measured.</summary>
    public TimeSpan DefaultWait { get; init; } = TimeSpan.FromSeconds(2.5);

    public TimeSpan MinWait { get; init; } = TimeSpan.FromSeconds(1.2);

    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>A burst is handed over at most this long after its first message, typing or not.</summary>
    public TimeSpan MaxBurst { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The base wait is this percentile of a person's measured gaps...</summary>
    public double GapPercentile { get; init; } = 0.9;

    /// <summary>...times this headroom.</summary>
    public double Headroom { get; init; } = 1.25;

    /// <summary>Measured gaps needed before the measurement replaces the default.</summary>
    public int MinSamples { get; init; } = 5;

    public int MaxSamples { get; init; } = 50;

    /// <summary>Wait multiplier after a message that looks complete.</summary>
    public double CompleteFactor { get; init; } = 0.5;

    /// <summary>Wait multiplier after a message that looks unfinished.</summary>
    public double UnfinishedFactor { get; init; } = 1.6;

    /// <summary>Surfaces whose messages are handed over at once, one per turn.</summary>
    public IReadOnlySet<string> ImmediateSurfaces { get; init; } = new HashSet<string> { "cli" };
}

/// <summary>
/// Groups each conversation's messages into bursts and hands a burst over once the person has
/// probably finished: after a wait measured from that person's own rhythm, adjusted by the shape of
/// their last message, and held open while the surface reports them typing.
/// </summary>
public sealed class TurnTaker : IDisposable
{
    private readonly Func<Burst, Task> _onBurst;
    private readonly TurnTakingOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Pending> _pending = new();
    private readonly Dictionary<string, Rhythm> _rhythms = new();
    private readonly object _gate = new();

    public TurnTaker(Func<Burst, Task> onBurst, TurnTakingOptions? options = null, TimeProvider? time = null)
    {
        _onBurst = onBurst;
        _options = options ?? new TurnTakingOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised when a burst could not be delivered.</summary>
    public event Action<Exception>? DeliveryFailed;

    public void Add(InboundMessage message)
    {
        if (_options.ImmediateSurfaces.Contains(message.Surface))
        {
            Deliver(new Burst(message.Surface, message.ConversationId, [message]));
            return;
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var rhythm = RhythmFor(message.Surface, message.SenderId);
            if (rhythm.LastMessageAt is { } previous)
            {
                var gap = now - previous;
                if (gap <= _options.MaxWait * 2)
                    rhythm.Record(gap, _options.MaxSamples);
            }
            rhythm.LastMessageAt = now;
            rhythm.LastConversation = message.ConversationId;

            var key = Key(message.Surface, message.ConversationId);
            if (!_pending.TryGetValue(key, out var pending))
            {
                pending = new Pending(message.Surface, message.ConversationId, now);
                _pending[key] = pending;
            }

            pending.Messages.Add(message);
            pending.WaitUntil = now + WaitAfter(message, rhythm);
            Schedule(key, pending, now);
        }
    }

    public void Typing(InboundTyping typing)
    {
        if (_options.ImmediateSurfaces.Contains(typing.Surface))
            return;

        lock (_gate)
        {
            var key = Key(typing.Surface, typing.ConversationId);
            if (!_pending.TryGetValue(key, out var pending))
                return;

            if (typing.ExpiresAt > pending.TypingUntil)
                pending.TypingUntil = typing.ExpiresAt;
            Schedule(key, pending, _time.GetUtcNow());
        }
    }

    /// <summary>
    /// The agent replied in a conversation. The next message there answers the reply rather than
    /// continuing the previous burst, so its gap says nothing about the person's rhythm.
    /// </summary>
    public void MarkAnswered(string surface, string conversationId)
    {
        lock (_gate)
        {
            foreach (var (key, rhythm) in _rhythms)
            {
                if (key.StartsWith(surface + ":", StringComparison.Ordinal) && rhythm.LastConversation == conversationId)
                    rhythm.LastMessageAt = null;
            }
        }
    }

    /// <summary>The wait currently used for a person before shape adjustments.</summary>
    public TimeSpan BaseWaitFor(string surface, string senderId)
    {
        lock (_gate)
            return BaseWait(RhythmFor(surface, senderId));
    }

    /// <summary>Whether a message reads as complete, unfinished, or neither.</summary>
    public static MessageShape ShapeOf(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return MessageShape.Neutral;

        if (trimmed.EndsWith("...", StringComparison.Ordinal) || trimmed.EndsWith('…')
            || trimmed[^1] is ',' or ':' or ';' or '-')
            return MessageShape.Unfinished;

        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var last = words[^1].ToLowerInvariant();
        if (Connectives.Contains(last))
            return MessageShape.Unfinished;

        if (trimmed.Length >= 200 || (trimmed.Length >= 40 && trimmed[^1] is '?' or '!' or '.'))
            return MessageShape.Complete;

        return trimmed[^1] is '?' ? MessageShape.Complete : MessageShape.Neutral;
    }

    /// <summary>Drop a conversation's waiting messages without delivering them.</summary>
    public void Discard(string surface, string conversationId)
    {
        lock (_gate)
        {
            var key = Key(surface, conversationId);
            if (_pending.Remove(key, out var pending))
                pending.Timer?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var pending in _pending.Values)
                pending.Timer?.Dispose();
            _pending.Clear();
        }
    }

    private static readonly HashSet<string> Connectives =
    [
        "y", "e", "o", "u", "pero", "que", "porque", "entonces", "además", "ademas", "osea",
        "and", "but", "or", "so", "because", "also", "then", "like",
    ];

    private TimeSpan WaitAfter(InboundMessage message, Rhythm rhythm)
    {
        var wait = BaseWait(rhythm);
        return ShapeOf(message.Text) switch
        {
            MessageShape.Complete => Max(wait * _options.CompleteFactor, _options.MinWait / 2),
            MessageShape.Unfinished => Min(wait * _options.UnfinishedFactor, _options.MaxWait * _options.UnfinishedFactor),
            _ => wait,
        };
    }

    private TimeSpan BaseWait(Rhythm rhythm)
    {
        if (rhythm.Gaps.Count < _options.MinSamples)
            return _options.DefaultWait;

        var sorted = rhythm.Gaps.OrderBy(g => g).ToList();
        var index = (int)Math.Ceiling(_options.GapPercentile * sorted.Count) - 1;
        var measured = sorted[Math.Clamp(index, 0, sorted.Count - 1)] * _options.Headroom;
        return Min(Max(measured, _options.MinWait), _options.MaxWait);
    }

    private void Schedule(string key, Pending pending, DateTimeOffset now)
    {
        var due = Deadline(pending) - now;
        if (due < TimeSpan.Zero)
            due = TimeSpan.Zero;

        if (pending.Timer is null)
            pending.Timer = _time.CreateTimer(_ => OnTimer(key), null, due, Timeout.InfiniteTimeSpan);
        else
            pending.Timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private DateTimeOffset Deadline(Pending pending)
    {
        var deadline = pending.WaitUntil > pending.TypingUntil ? pending.WaitUntil : pending.TypingUntil;
        var cap = pending.StartedAt + _options.MaxBurst;
        return deadline < cap ? deadline : cap;
    }

    private void OnTimer(string key)
    {
        Burst? burst = null;
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var pending))
                return;

            var now = _time.GetUtcNow();
            if (now < Deadline(pending))
            {
                Schedule(key, pending, now);
                return;
            }

            _pending.Remove(key);
            pending.Timer?.Dispose();
            burst = new Burst(pending.Surface, pending.ConversationId, pending.Messages.ToList());
        }

        Deliver(burst);
    }

    private void Deliver(Burst burst)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _onBurst(burst).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DeliveryFailed?.Invoke(ex);
            }
        });
    }

    private Rhythm RhythmFor(string surface, string senderId)
    {
        var key = Key(surface, senderId);
        if (!_rhythms.TryGetValue(key, out var rhythm))
        {
            rhythm = new Rhythm();
            _rhythms[key] = rhythm;
        }
        return rhythm;
    }

    private static string Key(string a, string b) => $"{a}:{b}";

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private sealed class Pending(string surface, string conversationId, DateTimeOffset startedAt)
    {
        public string Surface { get; } = surface;
        public string ConversationId { get; } = conversationId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public List<InboundMessage> Messages { get; } = [];
        public DateTimeOffset WaitUntil { get; set; }
        public DateTimeOffset TypingUntil { get; set; }
        public ITimer? Timer { get; set; }
    }

    private sealed class Rhythm
    {
        public Queue<TimeSpan> Gaps { get; } = new();
        public DateTimeOffset? LastMessageAt { get; set; }
        public string? LastConversation { get; set; }

        public void Record(TimeSpan gap, int maxSamples)
        {
            Gaps.Enqueue(gap);
            while (Gaps.Count > maxSamples)
                Gaps.Dequeue();
        }
    }
}

/// <summary>How a message reads with respect to whether the person has finished.</summary>
public enum MessageShape
{
    Neutral,
    Complete,
    Unfinished,
}
