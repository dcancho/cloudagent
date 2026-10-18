using CloudAgent.Core.Memory;
using CloudAgent.Core.Tests.Support;

namespace CloudAgent.Core.Tests.Memory;

public sealed class UtilityJudgeTests : IDisposable
{
    private readonly MemoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<MemoryEntry[]> ThreeMemoriesAsync() =>
    [
        await _fixture.AddAsync("Money uses decimal."),
        await _fixture.AddAsync("Round only at output."),
        await _fixture.AddAsync("Plants are watered on Fridays."),
    ];

    [Theory]
    [InlineData("[1,3]", new[] { 0, 2 })]
    [InlineData("Influential: [2] because it set the rule.", new[] { 1 })]
    [InlineData("[2,2,9,0]", new[] { 1 })]
    [InlineData("[]", new int[0])]
    public async Task Maps_the_model_numbers_to_memory_indices(string reply, int[] expected)
    {
        var model = new ScriptedLlmProvider().Otherwise(reply);
        var judge = new UtilityJudge(model);

        var useful = await judge.JudgeAsync(await ThreeMemoriesAsync(), "question", "answer");

        Assert.Equal(expected, useful);
    }

    [Theory]
    [InlineData("I think the first one.")]
    [InlineData("[1, two]")]
    public async Task An_unreadable_reply_is_no_verdict(string reply)
    {
        var judge = new UtilityJudge(new ScriptedLlmProvider().Otherwise(reply));

        Assert.Null(await judge.JudgeAsync(await ThreeMemoriesAsync(), "question", "answer"));
    }

    [Fact]
    public async Task A_failed_call_is_no_verdict()
    {
        var judge = new UtilityJudge(new ScriptedLlmProvider().Throws(_ => true));

        Assert.Null(await judge.JudgeAsync(await ThreeMemoriesAsync(), "question", "answer"));
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var model = new ScriptedLlmProvider().When(_ => true, _ => throw new OperationCanceledException());
        var judge = new UtilityJudge(model);
        var memories = await ThreeMemoriesAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => judge.JudgeAsync(memories, "question", "answer"));
    }

    [Fact]
    public async Task Nothing_to_judge_makes_no_call()
    {
        var model = new ScriptedLlmProvider().Otherwise("[1]");
        var judge = new UtilityJudge(model);

        Assert.Null(await judge.JudgeAsync([], "question", "answer"));
        Assert.Null(await judge.JudgeAsync(await ThreeMemoriesAsync(), "question", "  "));
        Assert.Empty(model.Requests);
    }
}
