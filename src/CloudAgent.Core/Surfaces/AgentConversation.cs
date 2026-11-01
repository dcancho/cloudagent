using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Loop;

namespace CloudAgent.Core.Surfaces;

/// <summary>
/// One conversation = one agent session: a pushable input queue feeding an <see cref="AgentLoop"/>,
/// whose textual replies are surfaced via <see cref="Responded"/>. The router posts inbound
/// messages in and ships <see cref="Responded"/> back out to the surface.
/// </summary>
public sealed class AgentConversation : IDisposable
{
    private readonly QueueInputSource _input;
    private readonly AgentLoop _loop;
    private readonly Action? _onDispose;
    private readonly object _gate = new();
    private readonly List<(string Text, string Receipt)> _awaiting = [];
    private string? _current;

    /// <param name="input">The queue the router posts inbound text to.</param>
    /// <param name="loop">The agent loop that reads it.</param>
    /// <param name="onDispose">Releases what the session holds once its loop has stopped.</param>
    public AgentConversation(QueueInputSource input, AgentLoop loop, Action? onDispose = null)
    {
        _input = input;
        _loop = loop;
        _onDispose = onDispose;
        _loop.ResponseReceived += (_, response) =>
        {
            if (!string.IsNullOrWhiteSpace(response.Text))
                Responded?.Invoke(response.Text);
        };
        _loop.InputReceived += text =>
        {
            lock (_gate)
            {
                var index = _awaiting.FindIndex(a => a.Text == text);
                _current = index < 0 ? null : _awaiting[index].Receipt;
                if (index >= 0)
                    _awaiting.RemoveAt(index);
            }
        };
        _loop.TurnEnded += _ =>
        {
            string? receipt;
            lock (_gate)
            {
                receipt = _current;
                _current = null;
            }
            if (receipt is not null)
                Settled?.Invoke(receipt);
        };
        _loop.ToolInvoked += (call, result) => Acted?.Invoke(ToolActivity.From(call, result));
        _loop.TurnFailed += (_, _) =>
            Responded?.Invoke("I couldn't finish that turn because of an error on my side. The details are in the host log.");
    }

    /// <summary>Raised with the agent's reply text for this conversation.</summary>
    public event Action<string>? Responded;

    /// <summary>Raised after each tool call the agent makes in this conversation.</summary>
    public event Action<ToolActivity>? Acted;

    /// <summary>Raised with a receipt once the turn that read the text posted under it has ended.
    /// A turn that was cancelled does not settle its receipt.</summary>
    public event Action<string>? Settled;

    /// <summary>Deliver inbound user text to the agent.</summary>
    public Task PostAsync(string text, CancellationToken cancellationToken = default) =>
        _input.PostAsync(text, cancellationToken);

    /// <summary>Deliver inbound user text and get <paramref name="receipt"/> back through
    /// <see cref="Settled"/> when the turn that read it ends.</summary>
    public Task PostAsync(string text, string receipt, CancellationToken cancellationToken)
    {
        lock (_gate)
            _awaiting.Add((text, receipt));
        return _input.PostAsync(text, cancellationToken);
    }

    /// <summary>Run the underlying loop until cancelled.</summary>
    public Task RunAsync(CancellationToken cancellationToken) => _loop.RunAsync(cancellationToken);

    public void Dispose() => _onDispose?.Invoke();
}
