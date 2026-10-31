using System;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.People;
using CloudAgent.Core.Surfaces;

namespace CloudAgent.Host;

/// <summary>
/// The console as a surface: stdin is one conversation per identity ("cli", or "cli-diego" when
/// started with --as diego), replies print to stdout.
/// Typing "exit" or "quit" stops the whole host. This unifies the terminal with every other
/// channel — the CLI is no longer special, just the surface you happen to be sitting at.
/// </summary>
/// <param name="stopSignal">Cancelled when the console asks to stop the host.</param>
/// <param name="personId">The person the console speaks as; null speaks as nobody in particular.</param>
public sealed class CliSurface(CancellationTokenSource stopSignal, string? personId = null) : ISurface
{

    public string Name => "cli";

    public async Task StartAsync(
        Func<InboundMessage, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
    {
        Console.WriteLine("Type a message and press Enter. Type 'exit' or Ctrl+C to stop.");

        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("\nyou> ");

            string? line;
            try
            {
                line = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null || line.Trim() is "exit" or "quit")
            {
                stopSignal.Cancel();
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            await onMessage(
                new InboundMessage(
                    Name, personId is null ? Name : $"{Name}-{personId}", ConversationKind.Direct,
                    personId ?? PeopleRegistry.AnonymousSender, personId ?? "you", line, DateTimeOffset.Now),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"\nagent> {message.Text}");
        return Task.CompletedTask;
    }
}
