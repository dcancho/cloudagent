using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Abstractions;

/// <summary>
/// The single seam between the agent loop and any LLM backend. Swap implementations
/// (Anthropic, OpenAI, a local model, a fake) without touching the loop.
/// </summary>
public interface ILlmProvider
{
    /// <summary>A short name for logging/diagnostics (e.g. "anthropic", "echo").</summary>
    string Name { get; }

    /// <summary>Send the working context to the model and get one completion back.</summary>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
}
