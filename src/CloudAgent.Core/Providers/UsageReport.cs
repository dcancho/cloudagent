using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CloudAgent.Core.Providers;

/// <summary>What a model charges, in dollars per million tokens.</summary>
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion);

/// <summary>Prices written as <c>model=input/output</c> pairs separated by <c>;</c>.</summary>
public static class ModelPrices
{
    /// <summary>Parse the pairs, matching model names without regard to case. Malformed pairs are skipped.</summary>
    public static IReadOnlyDictionary<string, ModelPrice> Parse(string? value)
    {
        var prices = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var halves = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            var rates = halves.Length == 2 ? halves[1].Split('/', StringSplitOptions.TrimEntries) : [];
            if (halves[0].Length > 0
                && rates.Length == 2
                && decimal.TryParse(rates[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var input)
                && decimal.TryParse(rates[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var output))
            {
                prices[halves[0]] = new ModelPrice(input, output);
            }
        }
        return prices;
    }
}

/// <summary>What a set of calls used.</summary>
/// <param name="Cost">Dollars, counting only calls whose model has a price.</param>
/// <param name="Unpriced">Some calls were on a model with no price, so <paramref name="Cost"/> is a floor.</param>
public sealed record UsageTotals(int Calls, int Failed, long InputTokens, long OutputTokens, decimal Cost, bool Unpriced)
{
    public static readonly UsageTotals None = new(0, 0, 0, 0, 0m, false);

    public long Tokens => InputTokens + OutputTokens;
}

/// <summary>Usage of one purpose, model, or agent.</summary>
public sealed record UsageGroup(string Key, UsageTotals Totals);

/// <summary>The ledger folded into totals, and into groups by purpose, model, and agent.</summary>
public sealed class UsageReport
{
    private UsageReport(
        UsageTotals total,
        IReadOnlyList<UsageGroup> byPurpose,
        IReadOnlyList<UsageGroup> byModel,
        IReadOnlyList<UsageGroup> byAgent)
    {
        Total = total;
        ByPurpose = byPurpose;
        ByModel = byModel;
        ByAgent = byAgent;
    }

    public UsageTotals Total { get; }

    public IReadOnlyList<UsageGroup> ByPurpose { get; }

    public IReadOnlyList<UsageGroup> ByModel { get; }

    /// <summary>Calls made outside any agent, such as the sleep timer's, are under <see cref="NoAgent"/>.</summary>
    public IReadOnlyList<UsageGroup> ByAgent { get; }

    public const string NoAgent = "(no agent)";

    public static UsageReport Build(IEnumerable<UsageRecord> records, IReadOnlyDictionary<string, ModelPrice> prices)
    {
        var all = records.ToList();
        return new UsageReport(
            Fold(all, prices),
            Group(all, r => r.Purpose, prices),
            Group(all, r => r.Model, prices),
            Group(all, r => r.Agent ?? NoAgent, prices));
    }

    private static IReadOnlyList<UsageGroup> Group(
        List<UsageRecord> records, Func<UsageRecord, string> key, IReadOnlyDictionary<string, ModelPrice> prices) =>
        records
            .GroupBy(key)
            .Select(g => new UsageGroup(g.Key, Fold(g, prices)))
            .OrderByDescending(g => g.Totals.Cost)
            .ThenByDescending(g => g.Totals.Tokens)
            .ToList();

    private static UsageTotals Fold(IEnumerable<UsageRecord> records, IReadOnlyDictionary<string, ModelPrice> prices)
    {
        var totals = UsageTotals.None;
        foreach (var record in records)
        {
            var input = record.InputTokens ?? 0;
            var output = record.OutputTokens ?? 0;
            var priced = prices.TryGetValue(record.Model, out var price);
            totals = totals with
            {
                Calls = totals.Calls + 1,
                Failed = totals.Failed + (record.Failed ? 1 : 0),
                InputTokens = totals.InputTokens + input,
                OutputTokens = totals.OutputTokens + output,
                Cost = totals.Cost + (priced ? (input * price!.InputPerMillion + output * price.OutputPerMillion) / 1_000_000m : 0m),
                Unpriced = totals.Unpriced || !priced,
            };
        }
        return totals;
    }
}
