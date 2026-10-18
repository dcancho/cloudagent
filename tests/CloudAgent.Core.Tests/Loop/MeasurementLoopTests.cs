using CloudAgent.Core.Attention;
using CloudAgent.Core.Loop;
using CloudAgent.Core.Memory;
using CloudAgent.Core.Tests.Support;

namespace CloudAgent.Core.Tests.Loop;

/// <summary>One real turn of the loop, with a scripted model, checked against what reaches the ledger.</summary>
public sealed class MeasurementLoopTests : IDisposable
{
    private const string Question = "How should payroll money round at output?";

    private readonly MemoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<(ScriptedLlmProvider Model, MemoryEntry Memory)> RunTurnAsync(Func<ScriptedLlmProvider, ScriptedLlmProvider> judge)
    {
        var memory = await _fixture.AddAsync("Payroll money rounds half away from zero at output.");
        var model = judge(new ScriptedLlmProvider()).Otherwise("Round half away from zero, only at output.");
        var context = new AgentContext();
        context.AddUser(Question);
        var loop = new AgentLoop(
            model,
            new AgentOptions { MaxIterations = 1, IterationDelay = TimeSpan.Zero },
            context: context,
            attention: new ContextAssembler("persona", new MemoryRetriever(_fixture.Memory), new AttentionOptions()),
            utility: new UtilityTracker(_fixture.Utility, new UtilityJudge(model), "agent"));

        await loop.RunAsync();
        return (model, memory);
    }

    [Fact]
    public async Task A_turn_records_the_injection_and_the_verdict()
    {
        var (model, memory) = await RunTurnAsync(m => m.When(ScriptedLlmProvider.IsJudge, "[1]"));

        var stat = _fixture.Utility.Stats[memory.Id];
        Assert.Equal(1, stat.Injections);
        Assert.Equal(1, stat.Useful);
        Assert.Contains(model.Requests, r => r.EphemeralContext?.Contains(memory.Content) == true);
    }

    [Fact]
    public async Task A_turn_records_unused_when_the_judge_says_so()
    {
        var (_, memory) = await RunTurnAsync(m => m.When(ScriptedLlmProvider.IsJudge, "[]"));

        var stat = _fixture.Utility.Stats[memory.Id];
        Assert.Equal(1, stat.Injections);
        Assert.Equal(0, stat.Useful);
        Assert.Equal(2, _fixture.Utility.Count);
    }

    [Fact]
    public async Task A_failing_judge_records_the_injection_only_and_the_turn_completes()
    {
        var (model, memory) = await RunTurnAsync(m => m.Throws(ScriptedLlmProvider.IsJudge));

        Assert.Equal(1, _fixture.Utility.Stats[memory.Id].Injections);
        Assert.Equal(1, _fixture.Utility.Count);
        Assert.Contains(model.Requests, r => !ScriptedLlmProvider.IsJudge(r));
    }

    [Fact]
    public async Task Memories_go_in_the_ephemeral_context_not_the_cached_system_prompt()
    {
        var (model, memory) = await RunTurnAsync(m => m.When(ScriptedLlmProvider.IsJudge, "[1]"));

        var main = model.Requests.First(r => !ScriptedLlmProvider.IsJudge(r));
        Assert.DoesNotContain(memory.Content, main.System);
        Assert.Contains(memory.Content, main.EphemeralContext);
    }
}
