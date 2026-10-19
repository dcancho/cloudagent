using CloudAgent.Core.Memory;
using CloudAgent.Core.Tests.Support;

namespace CloudAgent.Core.Tests.Memory;

public sealed class ConsolidationLedgerTests : IDisposable
{
    private readonly MemoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task WriteThreeAsync()
    {
        await _fixture.Ledger.AppendAsync("demote", "a -> Cold");
        await _fixture.Ledger.AppendAsync("promote", "b Active -> Resident");
        await _fixture.Ledger.AppendAsync("restore", "a -> Active");
    }

    [Fact]
    public async Task An_untouched_chain_verifies()
    {
        await WriteThreeAsync();

        Assert.True(_fixture.Ledger.Verify());
    }

    [Fact]
    public void An_empty_ledger_verifies()
    {
        Assert.True(_fixture.Ledger.Verify());
    }

    [Fact]
    public async Task Each_record_links_to_the_previous_hash()
    {
        var first = await _fixture.Ledger.AppendAsync("demote", "a");
        var second = await _fixture.Ledger.AppendAsync("demote", "b");

        Assert.Equal("", first.Prev);
        Assert.Equal(first.Hash, second.Prev);
    }

    [Fact]
    public async Task An_edited_record_breaks_the_chain()
    {
        await WriteThreeAsync();
        var text = File.ReadAllText(_fixture.LedgerPath);
        File.WriteAllText(_fixture.LedgerPath, text.Replace("\"b Active", "\"c Active"));

        Assert.False(_fixture.Ledger.Verify());
    }

    [Fact]
    public async Task A_removed_record_breaks_the_chain()
    {
        await WriteThreeAsync();
        var lines = File.ReadAllLines(_fixture.LedgerPath);
        File.WriteAllLines(_fixture.LedgerPath, [lines[0], lines[2]]);

        Assert.False(_fixture.Ledger.Verify());
    }

    [Fact]
    public async Task A_reopened_ledger_continues_the_chain()
    {
        await WriteThreeAsync();

        var reopened = new ConsolidationLedger(_fixture.LedgerPath);
        await reopened.AppendAsync("demote", "d -> Cold");

        Assert.True(reopened.Verify());
    }
}
