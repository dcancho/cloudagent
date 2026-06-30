using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Loop;

/// <summary>
/// Where the agent gets stimulus from the outside world each tick — a human, a queue,
/// a scheduler, a sensor. Returning null means "nothing right now": the agent keeps
/// running on its own initiative rather than blocking for input.
/// </summary>
public interface IInputSource
{
    Task<string?> NextAsync(CancellationToken cancellationToken = default);
}

/// <summary>An input source that never has anything — a purely self-driven agent.</summary>
public sealed class NullInputSource : IInputSource
{
    public Task<string?> NextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
