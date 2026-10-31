using CloudAgent.Core.Memory;
using CloudAgent.Core.Tests.Support;

namespace CloudAgent.Core.Tests.Memory;

public sealed class SleepJobTests : IDisposable
{
    private readonly MemoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<MemoryResidency?> ResidencyAsync(MemoryEntry entry) => (await _fixture.FindAsync(entry.Id))?.Residency;

    [Fact]
    public async Task Never_demotes_a_fact_to_cold_on_unused_judgments_alone()
    {
        var fact = await _fixture.AddAsync("Money uses decimal.");
        await _fixture.RecordAsync(fact.Id, injections: 6, agentId: "cli:a");
        await _fixture.RecordAsync(fact.Id, injections: 6, agentId: "cli:b");

        var result = await _fixture.Sleep().RunAsync();

        Assert.Equal(0, result.Demoted);
        Assert.Equal(MemoryResidency.Active, await ResidencyAsync(fact));
    }

    [Fact]
    public async Task A_fact_unused_in_one_context_is_still_recalled_in_another()
    {
        var fact = await _fixture.AddAsync("Payroll money rounds half away from zero at output.");
        await _fixture.RecordAsync(fact.Id, injections: 5, agentId: "cli:a");
        await _fixture.Sleep().RunAsync();

        var hits = await _fixture.Memory.RecallAsync("How does payroll money round at output?", MemoryFixture.AllScopes, 3);

        Assert.Contains(hits, hit => hit.Entry.Id == fact.Id);
    }

    [Fact]
    public async Task Leaves_a_fact_that_was_useful_once()
    {
        var fact = await _fixture.AddAsync("Money uses decimal.");
        await _fixture.RecordAsync(fact.Id, injections: 6, useful: 1);

        await _fixture.Sleep().RunAsync();

        Assert.Equal(MemoryResidency.Active, await ResidencyAsync(fact));
    }

    [Fact]
    public async Task Leaves_a_fact_with_no_record()
    {
        var fact = await _fixture.AddAsync("Money uses decimal.");

        await _fixture.Sleep().RunAsync();

        Assert.Equal(MemoryResidency.Active, await ResidencyAsync(fact));
    }

    [Fact]
    public async Task Demotes_an_episode_past_its_ttl_that_never_helped()
    {
        var old = await _fixture.AddAsync("User: hi", MemoryScope.Session, MemoryKind.Episodic, createdAt: DateTimeOffset.UtcNow.AddDays(-15));
        var recent = await _fixture.AddAsync("User: hello", MemoryScope.Session, MemoryKind.Episodic, createdAt: DateTimeOffset.UtcNow.AddDays(-13));
        var helped = await _fixture.AddAsync("User: the cap is ten hours", MemoryScope.Session, MemoryKind.Episodic, createdAt: DateTimeOffset.UtcNow.AddDays(-30));
        await _fixture.RecordAsync(helped.Id, injections: 1, useful: 1);

        await _fixture.Sleep().RunAsync();

        Assert.Equal(MemoryResidency.Cold, await ResidencyAsync(old));
        Assert.Equal(MemoryResidency.Active, await ResidencyAsync(recent));
        Assert.Equal(MemoryResidency.Active, await ResidencyAsync(helped));
    }

    [Fact]
    public async Task Promotes_a_consistently_useful_fact_to_resident()
    {
        var fact = await _fixture.AddAsync("Money uses decimal.");
        await _fixture.RecordAsync(fact.Id, injections: 4, useful: 2);

        var result = await _fixture.Sleep().RunAsync();

        Assert.Equal(1, result.Promoted);
        Assert.Equal(MemoryResidency.Resident, await ResidencyAsync(fact));
    }

    [Fact]
    public async Task Demotes_a_resident_whose_rate_decays()
    {
        var resident = await _fixture.AddAsync("Money uses decimal.", residency: MemoryResidency.Resident);
        await _fixture.RecordAsync(resident.Id, injections: 10, useful: 2);

        await _fixture.Sleep().RunAsync();

        Assert.Equal(MemoryResidency.Active, await ResidencyAsync(resident));
    }

    [Fact]
    public async Task Respects_the_resident_cap_and_promotes_the_best_rate_first()
    {
        var best = await _fixture.AddAsync("Fact with the best rate.");
        var second = await _fixture.AddAsync("Fact with a good rate.");
        await _fixture.RecordAsync(best.Id, injections: 4, useful: 4);
        await _fixture.RecordAsync(second.Id, injections: 4, useful: 3);

        await _fixture.Sleep(new SleepOptions { MaxResidents = 1 }).RunAsync();

        Assert.Equal(MemoryResidency.Resident, await ResidencyAsync(best));
        Assert.Equal(MemoryResidency.Active, await ResidencyAsync(second));
    }

    [Fact]
    public async Task Every_move_lands_in_a_verifiable_ledger()
    {
        var unused = await _fixture.AddAsync("User: hi", MemoryScope.Session, MemoryKind.Episodic, createdAt: DateTimeOffset.UtcNow.AddDays(-15));
        var useful = await _fixture.AddAsync("Money uses decimal.");
        await _fixture.RecordAsync(useful.Id, injections: 3, useful: 3);

        await _fixture.Sleep().RunAsync();

        var records = _fixture.ReadLedger();
        Assert.Contains(records, r => r.Action == "demote" && r.Detail.StartsWith($"{unused.Id} -> Cold"));
        Assert.Contains(records, r => r.Action == "promote" && r.Detail.StartsWith($"{useful.Id} Active -> Resident"));
        Assert.True(_fixture.Ledger.Verify());
    }
}
