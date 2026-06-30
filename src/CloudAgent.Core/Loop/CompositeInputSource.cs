using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CloudAgent.Core.Loop;

/// <summary>
/// Merges several input sources into one stream. The parent agent uses this to perceive both
/// its surface (the human) and its mailbox (child agents' questions/results) as one sequence of
/// turns. Each source is pumped on its own task into a shared channel.
/// </summary>
public sealed class CompositeInputSource : IInputSource
{
    private readonly IInputSource[] _sources;
    private readonly Channel<string> _merged =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private int _started;

    public CompositeInputSource(params IInputSource[] sources) => _sources = sources;

    public async Task<string?> NextAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            foreach (var source in _sources)
                _ = PumpAsync(source, cancellationToken);
        }

        try
        {
            return await _merged.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task PumpAsync(IInputSource source, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var item = await source.NextAsync(cancellationToken).ConfigureAwait(false);
                if (item is null)
                    break;
                await _merged.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
