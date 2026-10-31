using CloudAgent.Core.Memory;

namespace CloudAgent.Core.Tests.Memory;

public sealed class PendingDecisionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloudagent-tests", Guid.NewGuid().ToString("N"));
    private readonly PendingConsolidations _pending;

    public PendingDecisionTests() => _pending = new PendingConsolidations(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private Task Stage(string id) =>
        _pending.StageAsync(new ConsolidationProposal(id, DateTimeOffset.UtcNow, "discord:1", [], null, []));

    [Fact]
    public async Task A_decision_is_recorded_without_touching_the_proposal()
    {
        await Stage("abc");

        _pending.Decide("abc", PendingConsolidations.Decision.Approve);

        Assert.Equal(PendingConsolidations.Decision.Approve, _pending.Decisions()["abc"]);
        Assert.NotNull(_pending.Get("abc"));
    }

    [Fact]
    public void A_decision_on_an_unknown_proposal_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => _pending.Decide("nope", PendingConsolidations.Decision.Discard));
    }

    [Fact]
    public async Task A_decision_does_not_show_up_as_a_proposal_and_can_be_cleared()
    {
        await Stage("abc");
        _pending.Decide("abc", PendingConsolidations.Decision.Discard);

        Assert.Single(_pending.List());

        _pending.ClearDecision("abc");

        Assert.Empty(_pending.Decisions());
    }

    [Fact]
    public async Task The_latest_decision_wins()
    {
        await Stage("abc");
        _pending.Decide("abc", PendingConsolidations.Decision.Approve);
        _pending.Decide("abc", PendingConsolidations.Decision.Discard);

        Assert.Equal(PendingConsolidations.Decision.Discard, _pending.Decisions()["abc"]);
    }
}
