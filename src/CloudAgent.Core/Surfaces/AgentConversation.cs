using System;
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
        _loop.ToolInvoked += (call, result) => Acted?.Invoke(ToolActivity.From(call, result));
        _loop.TurnFailed += (_, _) =>
            Responded?.Invoke("I couldn't finish that turn because of an error on my side. The details are in the host log.");
    }

    /// <summary>Raised with the agent's reply text for this conversation.</summary>
    public event Action<string>? Responded;

    /// <summary>Raised after each tool call the agent makes in this conversation.</summary>
    public event Action<ToolActivity>? Acted;

    /// <summary>Deliver inbound user text to the agent.</summary>
    public Task PostAsync(string text, CancellationToken cancellationToken = default) =>
        _input.PostAsync(text, cancellationToken);

    /// <summary>Run the underlying loop until cancelled.</summary>
    public Task RunAsync(CancellationToken cancellationToken) => _loop.RunAsync(cancellationToken);

    public void Dispose() => _onDispose?.Invoke();
}
