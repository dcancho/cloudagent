using System.Text.Json.Nodes;
using CloudAgent.Core.Attention;

namespace CloudAgent.Core.Tests.Attention;

public sealed class RoutingOutcomeTests : IDisposable
{
    private readonly string _ledger = Path.Combine(Path.GetTempPath(), "cloudagent-tests", Guid.NewGuid().ToString("N"), "routing.jsonl");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_ledger)!;
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class NoDecisions : IDecisionModel
    {
        public string Name => "none";

        public Task<IReadOnlyList<DecisionAnswer>> DecideAsync(
            string situation, IReadOnlyList<DecisionQuestion> questions, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private ContextRouter Router() => new(new NoDecisions(), new RoutingOptions
    {
        LedgerPath = _ledger,
        ToolCategories = new Dictionary<string, string> { ["fetch_url"] = "web", ["read_file"] = "files" },
    });

    private static RoutingDecision Decision(MemoryLevel proposed, bool shadow, params string[] proposedCategories) =>
        new(MemoryLevel.Full, new HashSet<string> { "web", "files" }, proposed, proposedCategories.ToHashSet(), shadow, FellBack: false, TimeSpan.FromMilliseconds(12));

    private JsonObject LastRow() => JsonNode.Parse(File.ReadLines(_ledger).Last())!.AsObject();

    [Fact]
    public void A_shadow_turn_that_skipped_memory_which_proved_useful_is_a_memory_miss()
    {
        Router().RecordOutcome(Decision(MemoryLevel.None, shadow: true, "web", "files"), [], injected: 2, useful: 1);

        Assert.True(LastRow()["memoryMiss"]!.GetValue<bool>());
    }

    [Fact]
    public void A_non_shadow_turn_cannot_be_a_memory_miss()
    {
        Router().RecordOutcome(Decision(MemoryLevel.None, shadow: false, "web", "files"), [], injected: 2, useful: 1);

        Assert.False(LastRow()["memoryMiss"]!.GetValue<bool>());
    }

    [Fact]
    public void Using_a_tool_from_a_category_the_router_would_hide_is_a_tool_miss()
    {
        Router().RecordOutcome(Decision(MemoryLevel.Full, shadow: true, "files"), ["fetch_url", "read_file"], injected: 0, useful: null);

        var row = LastRow();
        Assert.True(row["toolMiss"]!.GetValue<bool>());
        Assert.Equal(["files", "web"], row["usedCategories"]!.AsArray().Select(n => n!.GetValue<string>()).Order());
    }

    [Fact]
    public void Failed_turns_are_recorded_with_their_outcome()
    {
        Router().RecordOutcome(Decision(MemoryLevel.Full, shadow: false, "web"), [], 0, null, TurnOutcome.Failed);

        Assert.Equal("Failed", LastRow()["outcome"]!.GetValue<string>());
    }
}
