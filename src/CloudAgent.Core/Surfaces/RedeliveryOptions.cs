using System;

namespace CloudAgent.Core.Surfaces;

/// <summary>Knobs for retrying the messages a restart interrupted.</summary>
public sealed class RedeliveryOptions
{
    /// <summary>The most times one message is redelivered, so one that keeps taking the host down gives up.</summary>
    public int MaxRedeliveries { get; init; } = 2;

    /// <summary>Messages older than this at startup are no longer worth answering.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long to wait for a surface to be able to send before leaving a conversation's messages for later.</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Sent before a conversation's messages are redelivered.</summary>
    public string Notice { get; init; } = "Me reinicié mientras procesaba tu mensaje. Lo retomo ahora.";

    /// <summary>Sent when a conversation's messages are too old to redeliver.</summary>
    public string StaleNotice { get; init; } =
        "Mi reinicio fue hace un rato, así que no retomé tu mensaje. Escríbemelo de nuevo si sigue importando.";
}
