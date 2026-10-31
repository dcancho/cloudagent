using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Loop;
using CloudAgent.Core.Providers;
using CloudAgent.Core.Tests.Support;

namespace CloudAgent.Core.Tests.Providers;

public sealed class UsageLedgerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloudagent-tests", Guid.NewGuid().ToString("N"));
    private readonly UsageLedger _ledger;

    public UsageLedgerTests() => _ledger = new UsageLedger(Path.Combine(_directory, "usage.jsonl"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static LlmRequest Request(string? purpose, string model = "small") =>
        new() { Messages = [Message.User("hi")], Model = model, Purpose = purpose };

    [Fact]
    public async Task Every_call_is_recorded_with_its_purpose_model_and_tokens()
    {
        var provider = new LedgeredLlmProvider(new ScriptedLlmProvider().Otherwise("ok"), _ledger);

        await provider.CompleteAsync(Request(LlmPurpose.Router, "decision"));
        await provider.CompleteAsync(Request(LlmPurpose.Turn, "main"));

        var records = _ledger.Read();
        Assert.Equal([LlmPurpose.Router, LlmPurpose.Turn], records.Select(r => r.Purpose));
        Assert.Equal(["decision", "main"], records.Select(r => r.Model));
        Assert.All(records, r =>
        {
            Assert.Equal(10, r.InputTokens);
            Assert.Equal(5, r.OutputTokens);
            Assert.False(r.Failed);
        });
    }

    [Fact]
    public async Task A_call_without_a_purpose_is_recorded_as_unlabeled()
    {
        var provider = new LedgeredLlmProvider(new ScriptedLlmProvider().Otherwise("ok"), _ledger);

        await provider.CompleteAsync(Request(purpose: null));

        Assert.Equal(LlmPurpose.Unlabeled, Assert.Single(_ledger.Read()).Purpose);
    }

    [Fact]
    public async Task A_failed_call_is_recorded_and_still_throws()
    {
        var provider = new LedgeredLlmProvider(new ScriptedLlmProvider().Throws(_ => true), _ledger);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteAsync(Request(LlmPurpose.Judge)));

        var record = Assert.Single(_ledger.Read());
        Assert.True(record.Failed);
        Assert.Null(record.InputTokens);
    }

    [Fact]
    public async Task A_cancelled_call_is_not_recorded()
    {
        var cancelled = new ScriptedLlmProvider().When(_ => true, _ => throw new OperationCanceledException());
        var provider = new LedgeredLlmProvider(cancelled, _ledger);

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.CompleteAsync(Request(LlmPurpose.Turn)));

        Assert.Empty(_ledger.Read());
    }

    [Fact]
    public async Task Calls_inside_a_scope_are_attributed_to_its_agent_and_only_there()
    {
        var provider = new LedgeredLlmProvider(new ScriptedLlmProvider().Otherwise("ok"), _ledger);

        using (UsageScope.Begin("discord:group-1"))
            await provider.CompleteAsync(Request(LlmPurpose.Turn));
        await provider.CompleteAsync(Request(LlmPurpose.Sleep));

        Assert.Equal(["discord:group-1", null], _ledger.Read().Select(r => r.Agent));
    }

    [Fact]
    public async Task A_running_loop_attributes_its_turn_to_its_agent_id()
    {
        var model = new ScriptedLlmProvider().Otherwise("hello");
        var context = new AgentContext();
        context.AddUser("hi");
        var loop = new AgentLoop(
            new LedgeredLlmProvider(model, _ledger),
            new AgentOptions { MaxIterations = 1, IterationDelay = TimeSpan.Zero },
            context: context) { AgentId = "webchat:abc" };

        await loop.RunAsync();

        var record = Assert.Single(_ledger.Read());
        Assert.Equal(LlmPurpose.Turn, record.Purpose);
        Assert.Equal("webchat:abc", record.Agent);
        Assert.Null(UsageScope.Agent);
    }

    [Fact]
    public void A_line_that_does_not_parse_loses_that_record_and_not_the_ledger()
    {
        _ledger.Append(new UsageRecord(DateTimeOffset.UtcNow, LlmPurpose.Turn, "m", null, 1, 1, null, null, TimeSpan.Zero, false));
        File.AppendAllText(Path.Combine(_directory, "usage.jsonl"), "{not json" + Environment.NewLine);
        _ledger.Append(new UsageRecord(DateTimeOffset.UtcNow, LlmPurpose.Judge, "m", null, 2, 2, null, null, TimeSpan.Zero, false));

        Assert.Equal([LlmPurpose.Turn, LlmPurpose.Judge], _ledger.Read().Select(r => r.Purpose));
    }

    [Fact]
    public async Task Concurrent_calls_each_leave_one_whole_line()
    {
        var provider = new LedgeredLlmProvider(new ScriptedLlmProvider().Otherwise("ok"), _ledger);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => provider.CompleteAsync(Request(LlmPurpose.Turn))));

        Assert.Equal(50, _ledger.Read().Count);
    }
}
