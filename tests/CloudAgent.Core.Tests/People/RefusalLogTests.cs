using CloudAgent.Core.People;

namespace CloudAgent.Core.Tests.People;

public sealed class RefusalLogTests
{
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly DateOnly Tuesday = new(2026, 10, 27);

    [Fact]
    public void Refusals_are_grouped_by_account_with_a_count_and_the_last_day()
    {
        var refusals = RefusalLog.Parse(
        [
            (Monday, "  ⛔ refused discord:485305305144754176 (jose)"),
            (Monday, "  ✉ conversation opened: discord:1"),
            (Tuesday, "  ⛔ refused discord:485305305144754176 (jose)"),
            (Tuesday, "  ⛔ refused telegram:99"),
        ]);

        var jose = Assert.Single(refusals, r => r.Account == "discord:485305305144754176");
        Assert.Equal(2, jose.Count);
        Assert.Equal(Tuesday, jose.LastDay);
        Assert.Equal("jose", jose.Name);
        Assert.Null(refusals.Single(r => r.Account == "telegram:99").Name);
    }

    [Fact]
    public void Lines_that_are_not_refusals_are_ignored()
    {
        var refusals = RefusalLog.Parse([(Monday, "People:    2 from /var/lib/cloudagent/people.json; web chat without identity refused")]);

        Assert.Empty(refusals);
    }

    [Fact]
    public void The_most_recent_refusal_comes_first()
    {
        var refusals = RefusalLog.Parse(
        [
            (Monday, "  ⛔ refused discord:1"),
            (Tuesday, "  ⛔ refused discord:2"),
        ]);

        Assert.Equal(["discord:2", "discord:1"], refusals.Select(r => r.Account));
    }
}
