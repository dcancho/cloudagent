using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CloudAgent.Core.Agents;

/// <summary>How an agent is currently doing.</summary>
public enum AgentStatus
{
    Starting,
    Running,
    WaitingForReply,
    Done,
    Failed,
    Stopped,
}

/// <summary>
/// The control surface for one live agent: its identity, its mailbox, its cancellation, and
/// the questions it is currently waiting on. Created and tracked by the <see cref="AgentBus"/>.
/// </summary>
public sealed class AgentHandle
{
    private readonly Channel<AgentMessage> _mailbox =
        Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();
    private readonly CancellationTokenSource _cts;

    internal AgentHandle(string id, string role, string? parentId, CancellationToken linkedTo)
    {
        Id = id;
        Role = role;
        ParentId = parentId;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);
    }

    public string Id { get; }
    public string Role { get; }
    public string? ParentId { get; }
    public AgentStatus Status { get; internal set; } = AgentStatus.Starting;

    /// <summary>The agent's final result, once it reports one.</summary>
    public string? Result { get; internal set; }

    /// <summary>The running loop's task, set by whoever starts it.</summary>
    public Task? Runner { get; set; }

    /// <summary>Cancelled when this agent (or an ancestor) is stopped.</summary>
    public CancellationToken Token => _cts.Token;

    internal ChannelReader<AgentMessage> MailboxReader => _mailbox.Reader;
    internal ChannelWriter<AgentMessage> MailboxWriter => _mailbox.Writer;

    internal void AddPending(string correlationId, TaskCompletionSource<string> tcs) =>
        _pending[correlationId] = tcs;

    internal bool TryCompletePending(string correlationId, string content)
    {
        if (_pending.TryRemove(correlationId, out var tcs))
            return tcs.TrySetResult(content);
        return false;
    }

    /// <summary>The oldest unanswered question's correlation id, if any.</summary>
    internal string? OldestPending() => _pending.IsEmpty ? null : _pending.Keys.FirstOrDefault();

    internal void Cancel()
    {
        _cts.Cancel();
        _mailbox.Writer.TryComplete();
        foreach (var tcs in _pending.Values)
            tcs.TrySetCanceled();
        _pending.Clear();
    }
}
