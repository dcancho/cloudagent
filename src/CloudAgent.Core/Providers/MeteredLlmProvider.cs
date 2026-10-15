using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Providers;

/// <summary>Token totals for one model since the process started.</summary>
/// <param name="Model">Model id.</param>
/// <param name="Calls">Completions made.</param>
/// <param name="InputTokens">Prompt tokens, including cached ones.</param>
/// <param name="OutputTokens">Completion tokens, including reasoning tokens where the provider counts them.</param>
public sealed record TokenUsage(string Model, int Calls, long InputTokens, long OutputTokens);

/// <summary>Counts the tokens every completion reports, per model, without changing the call.</summary>
public sealed class MeteredLlmProvider(ILlmProvider inner) : ILlmProvider
{
    private readonly ConcurrentDictionary<string, Counter> _counters = new();

    public string Name => inner.Name;

    /// <summary>Raised after each completion with its model and response.</summary>
    public event Action<string, LlmResponse>? Completed;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var response = await inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        var model = request.Model ?? "(default)";
        var counter = _counters.GetOrAdd(model, _ => new Counter());
        Interlocked.Increment(ref counter.Calls);
        Interlocked.Add(ref counter.Input, response.InputTokens ?? 0);
        Interlocked.Add(ref counter.Output, response.OutputTokens ?? 0);
        Completed?.Invoke(model, response);
        return response;
    }

    public IReadOnlyList<TokenUsage> Usage =>
        _counters
            .Select(c => new TokenUsage(c.Key, c.Value.Calls, Interlocked.Read(ref c.Value.Input), Interlocked.Read(ref c.Value.Output)))
            .OrderBy(u => u.Model)
            .ToList();

    private sealed class Counter
    {
        public int Calls;
        public long Input;
        public long Output;
    }
}
