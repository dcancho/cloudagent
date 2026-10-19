using CloudAgent.Core.Memory;
using CloudAgent.Core.Tests.Support;

namespace CloudAgent.Core.Tests.Memory;

public sealed class UtilityTrackerTests : IDisposable
{
    private readonly MemoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<MemoryHit[]> HitsAsync() =>
    [
        new(await _fixture.AddAsync("Money uses decimal."), 0.9),
        new(await _fixture.AddAsync("Plants are watered on Fridays."), 0.4),
    ];

    [Fact]
    public async Task Records_one_injection_per_memory()
    {
        var hits = await HitsAsync();
        var tracker = new UtilityTracker(_fixture.Utility, judge: null, "agent");

        await tracker.RecordInjectionAsync(hits, turn: 3);

        Assert.All(hits, hit => Assert.Equal(1, _fixture.Utility.Stats[hit.Entry.Id].Injections));
    }

    [Fact]
    public async Task Records_a_verdict_for_every_injected_memory()
    {
        var hits = await HitsAsync();
        var judge = new UtilityJudge(new ScriptedLlmProvider().Otherwise("[1]"));
        var tracker = new UtilityTracker(_fixture.Utility, judge, "agent");
        await tracker.RecordInjectionAsync(hits, turn: 1);

        var useful = await tracker.JudgeTurnAsync(hits, "question", "answer", turn: 1);

        Assert.Equal(1, useful);
        Assert.Equal(1.0, _fixture.Utility.Stats[hits[0].Entry.Id].UsefulRate);
        Assert.Equal(0.0, _fixture.Utility.Stats[hits[1].Entry.Id].UsefulRate);
        Assert.Equal(4, _fixture.Utility.Count);
    }

    [Fact]
    public async Task A_failed_judgment_records_nothing()
    {
        var hits = await HitsAsync();
        var judge = new UtilityJudge(new ScriptedLlmProvider().Throws(_ => true));
        var tracker = new UtilityTracker(_fixture.Utility, judge, "agent");

        var useful = await tracker.JudgeTurnAsync(hits, "question", "answer", turn: 1);

        Assert.Null(useful);
        Assert.Equal(0, _fixture.Utility.Count);
    }

    [Fact]
    public async Task Without_a_judge_there_is_no_verdict()
    {
        var tracker = new UtilityTracker(_fixture.Utility, judge: null, "agent");

        Assert.Null(await tracker.JudgeTurnAsync(await HitsAsync(), "question", "answer", turn: 1));
        Assert.Equal(0, _fixture.Utility.Count);
    }
}
