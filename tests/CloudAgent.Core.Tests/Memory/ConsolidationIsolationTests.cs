using CloudAgent.Core.Memory;
using CloudAgent.Core.Tests.Support;
using CloudAgent.Core.Tools;
using CloudAgent.Core.Tools.Memory;
using Xunit;

namespace CloudAgent.Core.Tests.Memory;

/// <summary>A conversation reviews only the proposals learned from it, never another person's.</summary>
public sealed class ConsolidationIsolationTests : IDisposable
{
    private readonly MemoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private Task StageAsync(string id, string agentId) => _fixture.Pending.StageAsync(new ConsolidationProposal(
        id, DateTimeOffset.UtcNow, agentId,
        [new DistilledFact("personal", $"fact learned in {agentId}", 0.6, "said so")], null, []));

    [Fact]
    public async Task Listing_shows_only_this_conversations_proposals()
    {
        await StageAsync("p1", "telegram:alice");
        await StageAsync("p2", "telegram:bob");

        var result = await new ListConsolidationsTool(_fixture.Pending, "telegram:bob").InvokeAsync(ToolInput.FromJson("{}"));

        Assert.Contains("p2", result.Content);
        Assert.DoesNotContain("p1", result.Content);
        Assert.DoesNotContain("alice", result.Content);
    }

    [Fact]
    public async Task Another_conversations_proposal_cannot_be_applied_or_discarded()
    {
        await StageAsync("p1", "telegram:alice");
        var tool = new ApplyConsolidationTool(_fixture.Pending, "telegram:bob", _fixture.Memory, _fixture.Ledger);

        var applied = await tool.InvokeAsync(ToolInput.FromJson("""{ "id": "p1" }"""));
        var discarded = await tool.InvokeAsync(ToolInput.FromJson("""{ "id": "p1", "discard": true }"""));

        Assert.False(applied.Success);
        Assert.False(discarded.Success);
        Assert.NotNull(_fixture.Pending.Get("p1"));
        Assert.Equal(0, _fixture.Store.Count);
    }
}
