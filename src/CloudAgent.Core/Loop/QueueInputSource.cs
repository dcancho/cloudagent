using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CloudAgent.Core.Loop;

/// <summary>
/// An input source you push into: stimulus is posted from outside (a surface, a router) and the
/// loop reads it when it perceives. Backed by an unbounded channel, so posting never blocks and
/// the loop blocks only while idle.
/// </summary>
public sealed class QueueInputSource : IInputSource
{
    private readonly Channel<string> _channel =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Hand the loop a new stimulus.</summary>
    public Task PostAsync(string text, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(text, cancellationToken).AsTask();

    public async Task<string?> NextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }
}
