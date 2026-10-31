using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Surfaces;

/// <summary>Knobs for deciding which group messages the agent answers.</summary>
public sealed class GroupAttentionOptions
{
    /// <summary>After the agent answers someone, their next messages for this long are taken as follow-ups.</summary>
    public TimeSpan FollowUp { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Most messages kept per group as context for the next turn the agent takes there.</summary>
    public int MaxHeld { get; init; } = 30;
}

/// <summary>What the agent does with a burst from a group.</summary>
public enum GroupVerdict
{
    /// <summary>Take a turn: the burst, and whatever was held before it, reaches the agent.</summary>
    Answer,

    /// <summary>Keep the burst as context for a later turn, without calling the model.</summary>
    Listen,
}

/// <summary>
/// Decides, without calling the main model, whether a group burst is for the agent. It answers when
/// the surface marks a message as explicitly addressed (a mention of its account, a reply to one of
/// its messages), or when someone it just answered keeps talking. A message that only says the
/// agent's name goes to the judge, when there is one, which tells speaking to the agent apart from
/// speaking about it; without a judge it answers, and the agent may still choose silence.
/// Everything else is held, so a later turn knows what the group was saying.
/// </summary>
public sealed class GroupAttention
{
    private readonly AgentIdentity? _identity;
    private readonly Func<string, CancellationToken, Task<bool>>? _judge;
    private readonly GroupAttentionOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Queue<string>> _held = new();
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> _answered = new();
    private readonly object _gate = new();

    /// <param name="identity">The agent's name; without one, only explicit addressing and follow-ups count.</param>
    /// <param name="judge">Given the recent group conversation, says whether its last messages speak to the
    /// agent rather than about it. It may throw; the burst is then answered.</param>
    public GroupAttention(
        AgentIdentity? identity,
        Func<string, CancellationToken, Task<bool>>? judge = null,
        GroupAttentionOptions? options = null,
        TimeProvider? time = null)
    {
        _identity = identity;
        _judge = judge;
        _options = options ?? new GroupAttentionOptions();
        _time = time ?? TimeProvider.System;
    }

    public AgentIdentity? Identity => _identity;

    /// <summary>Decide what a group burst gets. <paramref name="lines"/> is the burst as the agent reads it.</summary>
    public async Task<GroupVerdict> DecideAsync(Burst burst, string lines, CancellationToken cancellationToken = default)
    {
        if (burst.Messages.Any(m => m.Addressing == Addressing.Explicit) || IsFollowUp(burst))
            return GroupVerdict.Answer;

        if (_identity is null || !burst.Messages.Any(m => _identity.IsNamedIn(m.Text)))
            return GroupVerdict.Listen;

        if (_judge is null)
            return GroupVerdict.Answer;

        try
        {
            var situation = $"A group chat with an assistant called {_identity.Describe()}.\n\n" +
                            $"Earlier messages:\n{Held(Key(burst), 10)}\n\nLatest messages:\n{lines}";
            return await _judge(situation, cancellationToken).ConfigureAwait(false)
                ? GroupVerdict.Answer
                : GroupVerdict.Listen;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Without a verdict, answering is the safe side: the agent can still stay silent.
            return GroupVerdict.Answer;
        }
    }

    /// <summary>Keep a burst the agent did not answer, as context for its next turn in that group.</summary>
    public void Hold(string surface, string conversationId, string lines)
    {
        lock (_gate)
        {
            var key = Key(surface, conversationId);
            if (!_held.TryGetValue(key, out var held))
                _held[key] = held = new Queue<string>();
            foreach (var line in lines.Split('\n'))
            {
                held.Enqueue(line);
                while (held.Count > _options.MaxHeld)
                    held.Dequeue();
            }
        }
    }

    /// <summary>
    /// The text the agent receives for a turn: the burst, preceded by what was held since its last
    /// turn in that group, which is then cleared.
    /// </summary>
    public string Compose(string surface, string conversationId, string lines)
    {
        string? earlier;
        lock (_gate)
        {
            _held.Remove(Key(surface, conversationId), out var held);
            earlier = held is { Count: > 0 } ? string.Join('\n', held) : null;
        }

        return earlier is null
            ? lines
            : $"[Earlier in the group, not addressed to you]\n{earlier}\n\n[New]\n{lines}";
    }

    /// <summary>The agent answered these senders in a conversation; their next messages count as follow-ups.</summary>
    public void Answered(string surface, string conversationId, IEnumerable<string> senderIds)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var key = Key(surface, conversationId);
            if (!_answered.TryGetValue(key, out var senders))
                _answered[key] = senders = new Dictionary<string, DateTimeOffset>();
            foreach (var sender in senderIds)
                senders[sender] = now;
        }
    }

    /// <summary>Drop what is held and remembered for a conversation.</summary>
    public void Forget(string surface, string conversationId)
    {
        lock (_gate)
        {
            var key = Key(surface, conversationId);
            _held.Remove(key);
            _answered.Remove(key);
        }
    }

    private bool IsFollowUp(Burst burst)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            return _answered.TryGetValue(Key(burst), out var senders)
                && burst.Messages.Any(m => senders.TryGetValue(m.SenderId, out var at) && now - at <= _options.FollowUp);
        }
    }

    private string Held(string key, int last)
    {
        lock (_gate)
        {
            return _held.TryGetValue(key, out var held) && held.Count > 0
                ? string.Join('\n', held.TakeLast(last))
                : "(none)";
        }
    }

    private static string Key(Burst burst) => Key(burst.Surface, burst.ConversationId);

    private static string Key(string surface, string conversationId) => $"{surface}:{conversationId}";
}
