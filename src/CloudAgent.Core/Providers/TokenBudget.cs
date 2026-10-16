using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Providers;

/// <summary>
/// A limit on the tokens a workspace's agents may spend, input and output together, kept in a file
/// so it holds across restarts. Past <see cref="Limit"/> the agents wrap up and stop; a reserve of
/// 5% above it lets them say so, and past that every model call is refused.
/// </summary>
public sealed class TokenBudget
{
    private readonly string? _path;
    private readonly object _gate = new();
    private long _input;
    private long _output;
    private long _calls;

    /// <param name="limit">Tokens, input and output together.</param>
    /// <param name="path">Where spending is kept; null keeps it in memory only.</param>
    public TokenBudget(long limit, string? path = null)
    {
        Limit = limit;
        _path = path;
        if (path is not null && File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject saved)
        {
            _input = saved["inputTokens"]?.GetValue<long>() ?? 0;
            _output = saved["outputTokens"]?.GetValue<long>() ?? 0;
            _calls = saved["calls"]?.GetValue<long>() ?? 0;
        }
    }

    public long Limit { get; }

    public long Used => Interlocked.Read(ref _input) + Interlocked.Read(ref _output);

    /// <summary>The budget is spent: agents finish what they are saying and start nothing new.</summary>
    public bool Exhausted => Used >= Limit;

    /// <summary>The reserve is spent too: no model call is allowed.</summary>
    public bool Overdrawn => Used >= Limit + Limit / 20;

    public void Record(int? inputTokens, int? outputTokens)
    {
        lock (_gate)
        {
            _input += inputTokens ?? 0;
            _output += outputTokens ?? 0;
            _calls++;
            if (_path is null)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = new JsonObject
            {
                ["limit"] = Limit,
                ["inputTokens"] = _input,
                ["outputTokens"] = _output,
                ["calls"] = _calls,
                ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            };
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json.ToJsonString());
            File.Move(temp, _path, overwrite: true);
        }
    }
}

/// <summary>Thrown when a model call would spend past a <see cref="TokenBudget"/>'s reserve.</summary>
public sealed class TokenBudgetExceededException(long used, long limit)
    : Exception($"Token budget spent: {used:N0} of {limit:N0} tokens used.");

/// <summary>Counts every completion against a <see cref="TokenBudget"/> and refuses calls once it is overdrawn.</summary>
public sealed class BudgetedLlmProvider(ILlmProvider inner, TokenBudget budget) : ILlmProvider
{
    public string Name => inner.Name;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        if (budget.Overdrawn)
            throw new TokenBudgetExceededException(budget.Used, budget.Limit);

        var response = await inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        budget.Record(response.InputTokens, response.OutputTokens);
        return response;
    }
}
