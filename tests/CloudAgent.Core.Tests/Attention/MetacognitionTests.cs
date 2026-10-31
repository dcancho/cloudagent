using CloudAgent.Core.Attention;
using CloudAgent.Core.Loop;
using CloudAgent.Core.Memory;
using CloudAgent.Core.Tests.Support;

namespace CloudAgent.Core.Tests.Attention;

public sealed class MetacognitionTests : IDisposable
{
    private const string Query = "How should payroll money round at output?";

    private readonly MemoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private MetacognitiveAttentionManager Gate(List<MemoryHit>? suppressed = null, string agentId = "test")
    {
        var options = new AttentionOptions();
        var inner = new ContextAssembler("persona", new MemoryRetriever(_fixture.Memory), options);
        return new MetacognitiveAttentionManager(inner, _fixture.Utility, options, agentId, hit => suppressed?.Add(hit));
    }

    [Fact]
    public async Task Suppresses_a_memory_that_keeps_not_helping()
    {
        var memory = await _fixture.AddAsync("Payroll money rounds half away from zero at output.");
        await _fixture.RecordAsync(memory.Id, injections: 3);
        var suppressed = new List<MemoryHit>();

        var assembled = await Gate(suppressed).AssembleAsync(new AgentContext(), Query);

        Assert.DoesNotContain(assembled.InjectedMemories, hit => hit.Entry.Id == memory.Id);
        Assert.Contains(suppressed, hit => hit.Entry.Id == memory.Id);
        Assert.DoesNotContain(memory.Content, assembled.EphemeralContext ?? "");
    }

    [Fact]
    public async Task Keeps_a_memory_with_no_record()
    {
        var memory = await _fixture.AddAsync("Payroll money rounds half away from zero at output.");

        var assembled = await Gate().AssembleAsync(new AgentContext(), Query);

        Assert.Contains(assembled.InjectedMemories, hit => hit.Entry.Id == memory.Id);
    }

    [Fact]
    public async Task Keeps_a_memory_too_new_to_judge()
    {
        var memory = await _fixture.AddAsync("Payroll money rounds half away from zero at output.");
        await _fixture.RecordAsync(memory.Id, injections: 2);

        var assembled = await Gate().AssembleAsync(new AgentContext(), Query);

        Assert.Contains(assembled.InjectedMemories, hit => hit.Entry.Id == memory.Id);
    }

    [Fact]
    public async Task Keeps_a_memory_that_has_helped()
    {
        var memory = await _fixture.AddAsync("Payroll money rounds half away from zero at output.");
        await _fixture.RecordAsync(memory.Id, injections: 5, useful: 1);

        var assembled = await Gate().AssembleAsync(new AgentContext(), Query);

        Assert.Contains(assembled.InjectedMemories, hit => hit.Entry.Id == memory.Id);
        Assert.Contains(memory.Content, assembled.EphemeralContext);
    }

    [Fact]
    public async Task Suppresses_only_in_the_context_where_it_was_unused()
    {
        var memory = await _fixture.AddAsync("Payroll money rounds half away from zero at output.");
        await _fixture.RecordAsync(memory.Id, injections: 3, agentId: "cli:a");

        var there = await Gate(agentId: "cli:a").AssembleAsync(new AgentContext(), Query);
        var elsewhere = await Gate(agentId: "cli:b").AssembleAsync(new AgentContext(), Query);

        Assert.DoesNotContain(there.InjectedMemories, hit => hit.Entry.Id == memory.Id);
        Assert.Contains(elsewhere.InjectedMemories, hit => hit.Entry.Id == memory.Id);
    }

    [Fact]
    public async Task Evidence_from_different_contexts_does_not_add_up()
    {
        var memory = await _fixture.AddAsync("Payroll money rounds half away from zero at output.");
        await _fixture.RecordAsync(memory.Id, injections: 2, agentId: "cli:a");
        await _fixture.RecordAsync(memory.Id, injections: 2, agentId: "cli:b");

        var assembled = await Gate(agentId: "cli:a").AssembleAsync(new AgentContext(), Query);

        Assert.Contains(assembled.InjectedMemories, hit => hit.Entry.Id == memory.Id);
    }

    [Fact]
    public async Task Usefulness_elsewhere_does_not_lift_suppression_here()
    {
        var memory = await _fixture.AddAsync("Payroll money rounds half away from zero at output.");
        await _fixture.RecordAsync(memory.Id, injections: 3, agentId: "cli:a");
        await _fixture.RecordAsync(memory.Id, injections: 3, useful: 3, agentId: "cli:b");

        var assembled = await Gate(agentId: "cli:a").AssembleAsync(new AgentContext(), Query);

        Assert.DoesNotContain(assembled.InjectedMemories, hit => hit.Entry.Id == memory.Id);
    }
}
