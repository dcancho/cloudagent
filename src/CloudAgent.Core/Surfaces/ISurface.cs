using System;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Surfaces;

/// <summary>
/// A channel through which the agent talks to the world: CLI, Telegram, Discord, a web chat,
/// email, … Each adapter hides its own transport (long-poll, websocket, webhook) behind one
/// shape: start receiving (calling a handler per inbound message) and send replies out.
/// </summary>
public interface ISurface
{
    /// <summary>Short name, also used as the surface part of a conversation key.</summary>
    string Name { get; }

    /// <summary>
    /// Begin receiving. Runs until <paramref name="cancellationToken"/> trips, invoking
    /// <paramref name="onMessage"/> for each inbound message.
    /// </summary>
    Task StartAsync(
        Func<InboundMessage, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken);

    /// <summary>Deliver a message to a conversation on this surface.</summary>
    Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default);
}
