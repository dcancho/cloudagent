using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Providers;

/// <summary>The agent a model call is spent for, carried along the async flow that makes the call.</summary>
public static class UsageScope
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>The agent id in scope, or null outside any agent.</summary>
    public static string? Agent => Current.Value;

    /// <summary>Attribute every model call made inside the returned scope to <paramref name="agentId"/>.</summary>
    public static IDisposable Begin(string? agentId)
    {
        var previous = Current.Value;
        Current.Value = agentId;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>One model call: what it was for, who it was spent for, and what it used.</summary>
/// <param name="At">When the call finished.</param>
/// <param name="Purpose">What the call was for, see <see cref="LlmPurpose"/>.</param>
/// <param name="Model">Model id.</param>
/// <param name="Agent">The agent id (<c>surface:conversation</c>), null for work outside any agent.</param>
/// <param name="InputTokens">Prompt tokens as the provider reports them; null when it does not.</param>
/// <param name="OutputTokens">Completion tokens, including reasoning where the provider counts them.</param>
/// <param name="CacheReadTokens">Prompt tokens served from cache, when reported.</param>
/// <param name="CacheWriteTokens">Prompt tokens written to cache, when reported.</param>
/// <param name="Elapsed">Wall-clock time of the call.</param>
/// <param name="Failed">The call threw; the tokens are unknown and the call may still have been billed.</param>
/// <param name="Chars">Characters each part of the prompt took (see <see cref="PromptPart"/>); estimates how the input tokens divide.</param>
public sealed record UsageRecord(
    DateTimeOffset At,
    string Purpose,
    string Model,
    string? Agent,
    int? InputTokens,
    int? OutputTokens,
    int? CacheReadTokens,
    int? CacheWriteTokens,
    TimeSpan Elapsed,
    bool Failed,
    IReadOnlyDictionary<string, int>? Chars = null);

/// <summary>
/// Every model call, one JSON line each, append-only. Tokens are the raw measurement; cost is
/// worked out from them when read, so a price change never rewrites history.
/// </summary>
public sealed class UsageLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public UsageLedger(string path)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    public void Append(UsageRecord record)
    {
        var line = JsonSerializer.Serialize(record, JsonOptions);
        lock (_gate)
            File.AppendAllText(_path, line + Environment.NewLine);
    }

    /// <summary>Every record, oldest first. A line that does not parse is skipped.</summary>
    public IReadOnlyList<UsageRecord> Read()
    {
        if (!File.Exists(_path))
            return [];

        string[] lines;
        lock (_gate)
            lines = File.ReadAllLines(_path);

        var records = new List<UsageRecord>(lines.Length);
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            try
            {
                if (JsonSerializer.Deserialize<UsageRecord>(line, JsonOptions) is { Purpose: not null, Model: not null } record)
                    records.Add(record);
            }
            catch (JsonException)
            {
            }
        }
        return records;
    }
}

/// <summary>Writes every completion to a <see cref="UsageLedger"/>, failed ones included, without changing the call.</summary>
public sealed class LedgeredLlmProvider(ILlmProvider inner, UsageLedger ledger) : ILlmProvider
{
    public string Name => inner.Name;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var purpose = request.Purpose ?? LlmPurpose.Unlabeled;
        var model = request.Model ?? "(default)";
        try
        {
            var response = await inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            Write(purpose, model, started, response, failed: false, request);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Write(purpose, model, started, response: null, failed: true, request);
            throw;
        }
    }

    private void Write(string purpose, string model, long started, LlmResponse? response, bool failed, LlmRequest request) =>
        ledger.Append(new UsageRecord(
            DateTimeOffset.UtcNow,
            purpose,
            model,
            UsageScope.Agent,
            response?.InputTokens,
            response?.OutputTokens,
            response?.CacheReadTokens,
            response?.CacheWriteTokens,
            System.Diagnostics.Stopwatch.GetElapsedTime(started),
            failed,
            PromptComposition.Measure(request)));
}
