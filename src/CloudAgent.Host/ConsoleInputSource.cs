using System;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Loop;

namespace CloudAgent.Host;

/// <summary>
/// Feeds the agent from stdin. Blocks the perceive step until the human types a line —
/// an interactive REPL-style loop. Typing "exit" or "quit" cancels the run.
/// </summary>
public sealed class ConsoleInputSource(CancellationTokenSource stopSignal) : IInputSource
{
    public async Task<string?> NextAsync(CancellationToken cancellationToken = default)
    {
        Console.Write("\nyou> ");
        string? line;
        try
        {
            line = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (line is null || line.Trim() is "exit" or "quit")
        {
            stopSignal.Cancel();
            return null;
        }

        return string.IsNullOrWhiteSpace(line) ? null : line;
    }
}
