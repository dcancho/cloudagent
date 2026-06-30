using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Loop;

namespace CloudAgent.Core.Agents;

/// <summary>
/// The switchboard for a tree of agents: it registers agents, routes messages between their
/// mailboxes, correlates questions with answers, tracks status, and propagates stop down the
/// tree. Agents run concurrently as their own tasks; the bus is how they talk without anyone
/// blocking the whole loop.
/// </summary>
public sealed class AgentBus(CancellationToken rootToken = default)
{
    private readonly ConcurrentDictionary<string, AgentHandle> _agents = new();

    public event Action<AgentHandle>? Registered;
    public event Action<AgentHandle>? StatusChanged;

    /// <summary>Register a new agent and return its handle. Its mailbox/cancellation are ready to use.</summary>
    public AgentHandle Register(string role, string? parentId = null, string? id = null)
    {
        id ??= $"agent-{Guid.NewGuid().ToString("N")[..8]}";
        var parentToken = parentId is not null && _agents.TryGetValue(parentId, out var parent)
            ? parent.Token
            : rootToken;

        var handle = new AgentHandle(id, role, parentId, parentToken);
        if (!_agents.TryAdd(id, handle))
            throw new InvalidOperationException($"Agent id '{id}' already exists.");

        Registered?.Invoke(handle);
        return handle;
    }

    public bool TryGet(string id, out AgentHandle handle) => _agents.TryGetValue(id, out handle!);

    public IReadOnlyCollection<AgentHandle> All => _agents.Values.ToArray();

    public IEnumerable<AgentHandle> ChildrenOf(string id) =>
        _agents.Values.Where(a => a.ParentId == id);

    /// <summary>An input source that reads the given agent's mailbox.</summary>
    public MailboxInputSource InputFor(AgentHandle handle) => new(handle.MailboxReader);

    public void SetStatus(AgentHandle handle, AgentStatus status)
    {
        handle.Status = status;
        StatusChanged?.Invoke(handle);
    }

    /// <summary>
    /// Route a message. Answers are delivered straight to the waiting caller's pending question
    /// (bypassing the mailbox, so the caller doesn't have to be reading it); everything else is
    /// enqueued to the recipient's mailbox.
    /// </summary>
    public async Task SendAsync(AgentMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Kind == MessageKind.Answer &&
            message.CorrelationId is not null &&
            _agents.TryGetValue(message.To, out var asker) &&
            asker.TryCompletePending(message.CorrelationId, message.Content))
        {
            return;
        }

        if (!_agents.TryGetValue(message.To, out var recipient))
            throw new InvalidOperationException($"No agent '{message.To}' to deliver to.");

        await recipient.MailboxWriter.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Send a question to another agent and await its answer. The asking tool blocks here, but
    /// the asking agent is still a live actor — the answer is delivered directly to this call.
    /// </summary>
    public async Task<string> AskAsync(
        AgentHandle from, string to, string content, CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..8];
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        from.AddPending(correlationId, tcs);

        await SendAsync(new AgentMessage(from.Id, to, MessageKind.Question, content, correlationId),
            cancellationToken).ConfigureAwait(false);

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Answer a specific correlated question.</summary>
    public Task AnswerAsync(string from, string to, string correlationId, string content) =>
        SendAsync(new AgentMessage(from, to, MessageKind.Answer, content, correlationId));

    /// <summary>Answer whichever question an agent is currently waiting on (its oldest).</summary>
    public Task<bool> AnswerLatestAsync(string from, string askerId, string content)
    {
        if (_agents.TryGetValue(askerId, out var asker) && asker.OldestPending() is { } correlationId)
            return AnswerAsync(from, askerId, correlationId, content).ContinueWith(_ => true);

        return Task.FromResult(false);
    }

    /// <summary>Mark an agent finished with a result, then wind it (and any descendants) down.</summary>
    public void Complete(AgentHandle handle, string result)
    {
        SetStatus(handle, AgentStatus.Done);
        handle.Complete(result);

        foreach (var child in ChildrenOf(handle.Id).ToArray())
            Stop(child.Id);

        handle.Cancel();
    }

    /// <summary>Stop an agent and all of its descendants.</summary>
    public void Stop(string id)
    {
        if (!_agents.TryGetValue(id, out var handle))
            return;

        foreach (var child in ChildrenOf(id).ToArray())
            Stop(child.Id);

        SetStatus(handle, AgentStatus.Stopped);
        handle.Cancel();
    }
}
