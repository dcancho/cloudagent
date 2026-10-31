using CloudAgent.Core.Attention;
using CloudAgent.Core.Loop;
using CloudAgent.Core.Memory;
using CloudAgent.Core.Tests.Support;
using CloudAgent.Core.Tools;
using CloudAgent.Core.Tools.Memory;

namespace CloudAgent.Core.Tests.Memory;

public sealed class ColdRecoveryTests : IDisposable
{
    private const string MoneyText =
        "Payroll money amounts use decimal arithmetic and round half away from zero only at output, " +
        "never in intermediate steps, so totals reconcile with the bank export to the cent.";

    private const string Query = "How should payroll money amounts round decimal totals at output?";

    private readonly MemoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<MemoryEntry> ColdMoneyAsync()
    {
        await _fixture.AddAsync("The office plants are watered on Fridays by the facilities team.");
        await _fixture.AddAsync("Diego prefers short commit messages with a verb prefix.", MemoryScope.Personal);
        return await _fixture.AddAsync(MoneyText, residency: MemoryResidency.Cold);
    }

    private ContextAssembler Assembler(AttentionOptions? options = null) =>
        new("persona", new MemoryRetriever(_fixture.Memory), options ?? new AttentionOptions());

    private RestoreMemoryTool Restore() => new(_fixture.Memory, _fixture.Utility, _fixture.Ledger, "agent", MemoryFixture.AllScopes);

    private static ToolInput Input(string json) => ToolInput.FromJson(json);

    [Fact]
    public async Task A_matching_cold_memory_leaves_a_stub_not_its_content()
    {
        var money = await ColdMoneyAsync();

        var assembled = await Assembler().AssembleAsync(new AgentContext(), Query);

        Assert.Contains(assembled.ColdStubs, hit => hit.Entry.Id == money.Id);
        Assert.DoesNotContain(assembled.InjectedMemories, hit => hit.Entry.Id == money.Id);
        Assert.Contains($"[{money.Id}]", assembled.EphemeralContext);
        Assert.Contains("restore_memory", assembled.EphemeralContext);
        Assert.DoesNotContain(MoneyText, assembled.EphemeralContext);
    }

    [Fact]
    public async Task An_unrelated_query_leaves_no_stub()
    {
        var money = await ColdMoneyAsync();

        var assembled = await Assembler().AssembleAsync(new AgentContext(), "When are the office plants watered?");

        Assert.DoesNotContain(assembled.ColdStubs, hit => hit.Entry.Id == money.Id);
    }

    [Fact]
    public async Task A_zero_stub_limit_keeps_cold_out()
    {
        await ColdMoneyAsync();

        var assembled = await Assembler(new AttentionOptions { ColdStubLimit = 0 }).AssembleAsync(new AgentContext(), Query);

        Assert.Empty(assembled.ColdStubs);
    }

    [Fact]
    public async Task Stubs_are_not_recorded_as_injections()
    {
        var money = await ColdMoneyAsync();
        var model = new ScriptedLlmProvider().Otherwise("Decimal, rounded at output.");
        var context = new AgentContext();
        context.AddUser(Query);
        var loop = new AgentLoop(model, new AgentOptions { MaxIterations = 1 }, context: context,
            attention: Assembler(), utility: new UtilityTracker(_fixture.Utility, judge: null, "agent"));

        await loop.RunAsync();

        Assert.False(_fixture.Utility.Stats.ContainsKey(money.Id));
    }

    [Fact]
    public async Task Search_lists_cold_memories_only_when_archived()
    {
        var money = await ColdMoneyAsync();
        var search = new SearchMemoryTool(_fixture.Memory, MemoryFixture.AllScopes);

        var archived = await search.InvokeAsync(Input($$"""{"query":"{{Query}}","archived":true}"""));
        var active = await search.InvokeAsync(Input($$"""{"query":"{{Query}}"}"""));

        Assert.Contains($"[{money.Id}]", archived.Content);
        Assert.DoesNotContain(MoneyText, archived.Content);
        Assert.DoesNotContain(money.Id, active.Content);
    }

    [Fact]
    public async Task Restore_brings_the_memory_back_and_starts_its_record_over()
    {
        var money = await ColdMoneyAsync();
        await _fixture.RecordAsync(money.Id, injections: 3);

        var result = await Restore().InvokeAsync(Input($$"""{"id":"{{money.Id}}","reason":"new module needs the rule"}"""));

        Assert.True(result.Success);
        Assert.Contains(MoneyText, result.Content);
        Assert.Equal(MemoryResidency.Active, (await _fixture.FindAsync(money.Id))?.Residency);
        var stat = _fixture.Utility.Stats[money.Id];
        Assert.Equal(0, stat.Injections);
        Assert.NotNull(stat.RestoredAt);
        Assert.Contains(_fixture.ReadLedger(), r => r.Action == "restore" && r.Detail.EndsWith("new module needs the rule"));
        Assert.True(_fixture.Ledger.Verify());
    }

    [Fact]
    public async Task Restore_rejects_an_unknown_id_and_ignores_an_active_memory()
    {
        var active = await _fixture.AddAsync("Money uses decimal.");

        var unknown = await Restore().InvokeAsync(Input("""{"id":"nope","reason":"x"}"""));
        var noop = await Restore().InvokeAsync(Input($$"""{"id":"{{active.Id}}","reason":"x"}"""));

        Assert.False(unknown.Success);
        Assert.Contains("not set aside", noop.Content);
        Assert.False(_fixture.Utility.Stats.ContainsKey(active.Id));
    }

    [Fact]
    public async Task A_restored_memory_is_injected_and_not_suppressed()
    {
        var money = await ColdMoneyAsync();
        await _fixture.RecordAsync(money.Id, injections: 3);
        await Restore().InvokeAsync(Input($$"""{"id":"{{money.Id}}","reason":"x"}"""));
        var options = new AttentionOptions();
        var gate = new MetacognitiveAttentionManager(Assembler(options), _fixture.Utility, options, "test");

        var assembled = await gate.AssembleAsync(new AgentContext(), Query);

        Assert.Contains(assembled.InjectedMemories, hit => hit.Entry.Id == money.Id);
        Assert.Empty(assembled.ColdStubs);
    }

    [Fact]
    public async Task Sleep_does_not_re_demote_on_the_old_record()
    {
        var episode = await _fixture.AddAsync("User: the overtime cap is ten hours.", MemoryScope.Session,
            MemoryKind.Episodic, MemoryResidency.Cold, DateTimeOffset.UtcNow.AddDays(-30));
        await Restore().InvokeAsync(Input($$"""{"id":"{{episode.Id}}","reason":"x"}"""));

        await _fixture.Sleep().RunAsync();

        Assert.Equal(MemoryResidency.Active, (await _fixture.FindAsync(episode.Id))?.Residency);
    }

    [Theory]
    [InlineData("alpha beta gamma delta", 12, "alpha beta…")]
    [InlineData("short", 12, "short")]
    [InlineData("line one\nline two", 40, "line one line two")]
    public void Preview_cuts_at_a_word_boundary_on_one_line(string content, int max, string expected)
    {
        Assert.Equal(expected, AttentionRendering.Preview(content, max));
    }
}
