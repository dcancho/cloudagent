using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CloudAgent.Core.Loop;

namespace CloudAgent.Core.Agents;

/// <summary>
/// Bridges an agent's mailbox to the loop's input seam: each iteration the agent blocks until
/// a message arrives, then perceives it as a stimulus. This is what makes inter-agent messages
/// "just input" — a child asking the parent a question is, to the parent's loop, a normal turn.
/// The most recent message is exposed so reply tools can correlate the answer.
/// </summary>
public sealed class MailboxInputSource(ChannelReader<AgentMessage> reader) : IInputSource
{
    /// <summary>The message delivered on the last <see cref="NextAsync"/> — for reply correlation.</summary>
    public AgentMessage? Last { get; private set; }

    public async Task<string?> NextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var message = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            Last = message;
            return Render(message);
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

    private static string Render(AgentMessage m) => m.Kind switch
    {
        MessageKind.Task => $"[task from {m.From}] {m.Content}",
        MessageKind.Question => $"[question from {m.From}] {m.Content}",
        MessageKind.Answer => $"[answer from {m.From}] {m.Content}",
        MessageKind.Progress => $"[progress from {m.From}] {m.Content}",
        MessageKind.Result => $"[result from {m.From}] {m.Content}",
        MessageKind.Error => $"[error from {m.From}] {m.Content}",
        _ => $"[message from {m.From}] {m.Content}",
    };
}
