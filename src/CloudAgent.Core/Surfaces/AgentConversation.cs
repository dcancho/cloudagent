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
            var text = SilentReply.Strip(response.Text);
            if (!string.IsNullOrWhiteSpace(text))
                Responded?.Invoke(text);
        };
        _loop.TurnEnded += _ => TurnEnded?.Invoke();
        _loop.ToolInvoked += (call, result) => Acted?.Invoke(ToolActivity.From(call, result));
        _loop.TurnFailed += (_, _) =>
            Responded?.Invoke("I couldn't finish that turn because of an error on my side. The details are in the host log.");
    }

    /// <summary>Raised with the agent's reply text for this conversation.</summary>
    public event Action<string>? Responded;

    /// <summary>Raised when the agent has finished a turn, whether it replied or stayed silent.</summary>
    public event Action? TurnEnded;

    /// <summary>Raised after each tool call the agent makes in this conversation.</summary>
    public event Action<ToolActivity>? Acted;

    /// <summary>Deliver inbound user text to the agent.</summary>
    public Task PostAsync(string text, CancellationToken cancellationToken = default) =>
        _input.PostAsync(text, cancellationToken);

    /// <summary>Run the underlying loop until cancelled.</summary>
    public Task RunAsync(CancellationToken cancellationToken) => _loop.RunAsync(cancellationToken);

    public void Dispose() => _onDispose?.Invoke();
}

/// <summary>
/// The marker an agent replies with when a group message is not for it. A reply that is only the
/// marker sends nothing; the marker is also removed from a reply that carries other text.
/// </summary>
public static class SilentReply
{
    public const string Marker = "<silent/>";

    /// <summary>The reply without the marker; empty when the agent chose to stay silent.</summary>
    public static string Strip(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace(Marker, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
}
