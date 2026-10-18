using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Loop;
using CloudAgent.Core.Providers;
using CloudAgent.Core.Tests.Support;

namespace CloudAgent.Core.Tests.Providers;

public sealed class TokenBudgetTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "cloudagent-tests", Guid.NewGuid().ToString("N"), "usage.json");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static LlmRequest Request => new() { Messages = [Message.User("hi")] };

    [Fact]
    public void Exhausted_at_the_limit_and_overdrawn_past_the_reserve()
    {
        var budget = new TokenBudget(1000);

        budget.Record(600, 399);
        Assert.False(budget.Exhausted);

        budget.Record(1, 0);
        Assert.True(budget.Exhausted);
        Assert.False(budget.Overdrawn);

        budget.Record(50, 0);
        Assert.True(budget.Overdrawn);
    }

    [Fact]
    public void Spending_survives_a_restart()
    {
        new TokenBudget(1000, _path).Record(300, 200);

        var reopened = new TokenBudget(1000, _path);

        Assert.Equal(500, reopened.Used);
    }

    [Fact]
    public async Task The_budgeted_provider_counts_every_call()
    {
        var budget = new TokenBudget(1000);
        var provider = new BudgetedLlmProvider(new ScriptedLlmProvider().Otherwise("ok"), budget);

        await provider.CompleteAsync(Request);
        await provider.CompleteAsync(Request);

        Assert.Equal(30, budget.Used);
    }

    [Fact]
    public async Task The_budgeted_provider_refuses_once_overdrawn()
    {
        var budget = new TokenBudget(100);
        budget.Record(105, 0);
        var model = new ScriptedLlmProvider().Otherwise("ok");
        var provider = new BudgetedLlmProvider(model, budget);

        await Assert.ThrowsAsync<TokenBudgetExceededException>(() => provider.CompleteAsync(Request));
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task An_exhausted_budget_answers_without_calling_the_model()
    {
        var budget = new TokenBudget(100);
        budget.Record(100, 0);
        var model = new ScriptedLlmProvider().Otherwise("ok");
        var input = new QueueInputSource();
        await input.PostAsync("do more work");
        var loop = new AgentLoop(model, new AgentOptions { MaxIterations = 1, IterationDelay = TimeSpan.Zero, Budget = budget }, input);

        await loop.RunAsync();

        Assert.Empty(model.Requests);
        Assert.Contains("token budget", loop.Context.Last?.Content);
    }
}
