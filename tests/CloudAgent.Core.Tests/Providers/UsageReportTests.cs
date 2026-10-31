using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Providers;

namespace CloudAgent.Core.Tests.Providers;

public sealed class UsageReportTests
{
    private static UsageRecord Call(string purpose, string model, string? agent, int input, int output, bool failed = false) =>
        new(DateTimeOffset.UtcNow, purpose, model, agent, input, output, null, null, TimeSpan.Zero, failed);

    private static readonly IReadOnlyDictionary<string, ModelPrice> Prices =
        ModelPrices.Parse("main=2/10;Small=0.5/1");

    [Fact]
    public void Prices_are_read_as_dollars_per_million_input_and_output_without_regard_to_case()
    {
        Assert.Equal(new ModelPrice(0.5m, 1m), Prices["small"]);
        Assert.Equal(new ModelPrice(2m, 10m), Prices["MAIN"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("main")]
    [InlineData("main=2")]
    [InlineData("main=a/b")]
    [InlineData("=1/2")]
    public void A_malformed_price_is_skipped(string value) => Assert.Empty(ModelPrices.Parse(value));

    [Fact]
    public void Cost_is_tokens_times_the_model_price()
    {
        var report = UsageReport.Build([Call(LlmPurpose.Turn, "main", "a", 1_000_000, 500_000)], Prices);

        Assert.Equal(7m, report.Total.Cost);
        Assert.False(report.Total.Unpriced);
    }

    [Fact]
    public void Calls_are_grouped_by_purpose_model_and_agent()
    {
        var report = UsageReport.Build(
        [
            Call(LlmPurpose.Turn, "main", "discord:g", 100, 10),
            Call(LlmPurpose.Turn, "main", "discord:g", 100, 10),
            Call(LlmPurpose.Router, "small", "discord:g", 20, 2),
            Call(LlmPurpose.Sleep, "small", null, 50, 5),
        ], Prices);

        Assert.Equal(2, report.ByPurpose.Single(g => g.Key == LlmPurpose.Turn).Totals.Calls);
        Assert.Equal(2, report.ByModel.Single(g => g.Key == "small").Totals.Calls);
        Assert.Equal(3, report.ByAgent.Single(g => g.Key == "discord:g").Totals.Calls);
        Assert.Equal(1, report.ByAgent.Single(g => g.Key == UsageReport.NoAgent).Totals.Calls);
        Assert.Equal(4, report.Total.Calls);
    }

    [Fact]
    public void A_model_without_a_price_makes_the_cost_a_floor_and_is_flagged()
    {
        var report = UsageReport.Build(
        [
            Call(LlmPurpose.Turn, "main", "a", 1_000_000, 0),
            Call(LlmPurpose.Router, "mystery", "a", 1_000_000, 0),
        ], Prices);

        Assert.Equal(2m, report.Total.Cost);
        Assert.True(report.Total.Unpriced);
        Assert.True(report.ByModel.Single(g => g.Key == "mystery").Totals.Unpriced);
        Assert.False(report.ByModel.Single(g => g.Key == "main").Totals.Unpriced);
    }

    [Fact]
    public void A_failed_call_counts_as_a_call_and_a_failure_with_no_tokens()
    {
        var report = UsageReport.Build(
            [new UsageRecord(DateTimeOffset.UtcNow, LlmPurpose.Judge, "main", null, null, null, null, null, TimeSpan.Zero, true)], Prices);

        Assert.Equal(1, report.Total.Calls);
        Assert.Equal(1, report.Total.Failed);
        Assert.Equal(0, report.Total.Tokens);
    }

    [Fact]
    public void The_costliest_group_comes_first()
    {
        var report = UsageReport.Build(
        [
            Call(LlmPurpose.Router, "small", "a", 1000, 100),
            Call(LlmPurpose.Turn, "main", "a", 1_000_000, 100_000),
        ], Prices);

        Assert.Equal([LlmPurpose.Turn, LlmPurpose.Router], report.ByPurpose.Select(g => g.Key));
    }
}
