using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Providers;

/// <summary>
/// A zero-dependency provider that fakes a completion. Lets the whole harness run end
/// to end with no API key — useful for wiring, tests, and offline development.
/// </summary>
public sealed class EchoLlmProvider : ILlmProvider
{
    public string Name => "echo";

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var lastUser = request.Messages.LastOrDefault(m => m.Role == Role.User);
        var text = lastUser is null
            ? "(echo) I have no input yet, so I'll just sit here and think."
            : $"(echo) You said: \"{lastUser.Content}\". I'd act on that if I had real tools.";

        return Task.FromResult(new LlmResponse(text, StopReason.EndTurn));
    }
}
