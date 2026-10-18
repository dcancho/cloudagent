using CloudAgent.Core.Memory;
using CloudAgent.Core.Tests.Support;

namespace CloudAgent.Core.Tests.Memory;

public sealed class UtilityLedgerTests : IDisposable
{
    private readonly MemoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Folds_injections_and_useful_verdicts_into_stats()
    {
        await _fixture.RecordAsync("m1", injections: 4, useful: 1);

        var stat = _fixture.Utility.Stats["m1"];
        Assert.Equal(4, stat.Injections);
        Assert.Equal(1, stat.Useful);
        Assert.Equal(0.25, stat.UsefulRate);
        Assert.Equal(8, _fixture.Utility.Count);
    }

    [Fact]
    public void Useful_rate_is_zero_without_injections()
    {
        Assert.Equal(0, new MemoryUtilityStats().UsefulRate);
    }

    [Fact]
    public async Task Replaying_the_file_rebuilds_the_same_stats()
    {
        await _fixture.RecordAsync("m1", injections: 3, useful: 2);
        await _fixture.RecordAsync("m2", injections: 5);

        var replayed = new FileUtilityLedger(_fixture.UtilityPath);

        Assert.Equal(_fixture.Utility.Count, replayed.Count);
        foreach (var id in new[] { "m1", "m2" })
        {
            Assert.Equal(_fixture.Utility.Stats[id].Injections, replayed.Stats[id].Injections);
            Assert.Equal(_fixture.Utility.Stats[id].Useful, replayed.Stats[id].Useful);
        }
    }

    [Fact]
    public async Task A_corrupt_line_loses_one_event_not_the_ledger()
    {
        await _fixture.RecordAsync("m1", injections: 2);
        File.AppendAllText(_fixture.UtilityPath, "{not json\n");
        await File.AppendAllLinesAsync(_fixture.UtilityPath, [File.ReadLines(_fixture.UtilityPath).First()]);

        var replayed = new FileUtilityLedger(_fixture.UtilityPath);

        Assert.Equal(3, replayed.Stats["m1"].Injections);
    }

    [Fact]
    public async Task Restored_signal_starts_the_record_over()
    {
        await _fixture.RecordAsync("m1", injections: 5);
        var at = DateTimeOffset.UtcNow;

        await _fixture.Utility.AppendAsync([new UtilityEvent("m1", "test", 0, at, UtilitySignal.Restored, "why")]);
        await _fixture.RecordAsync("m1", injections: 1, useful: 1);

        var stat = _fixture.Utility.Stats["m1"];
        Assert.Equal(1, stat.Injections);
        Assert.Equal(1, stat.Useful);
        Assert.Equal(at, stat.RestoredAt);
    }
}
