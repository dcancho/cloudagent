using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Loop;
using CloudAgent.Core.Tests.Support;
using CloudAgent.Core.Tools;
using CloudAgent.Core.Tools.Web;

namespace CloudAgent.Core.Tests.Loop;

public sealed class ToolBudgetTests
{
    private sealed class StubTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "stub";
        public IReadOnlyList<ToolParameter> Parameters => [];
        public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Ok("result"));
    }

    private sealed class Hits(params WebSearchHit[] hits) : IWebSearchProvider
    {
        public string Name => "stub";
        public Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WebSearchHit>>(hits);
    }

    private static async Task<(ScriptedLlmProvider Model, int Calls, string? Reply)> RunAsync(
        string tool, int points, int callsPerStep = 1, int steps = 16)
    {
        var model = new ScriptedLlmProvider()
            .When(r => r.Tools.Count > 0, _ => new LlmResponse("")
            {
                ToolCalls = Enumerable.Range(0, callsPerStep).Select(i => new ToolCall(Guid.NewGuid().ToString("N"), tool, "{}")).ToList(),
            })
            .Otherwise("done");
        var context = new AgentContext();
        context.AddUser("go");
        var tools = new ToolRegistry().Register(new StubTool(tool));
        var loop = new AgentLoop(
            model,
            new AgentOptions { MaxIterations = 1, IterationDelay = TimeSpan.Zero, MaxToolPoints = points, MaxToolSteps = steps },
            context: context,
            tools: tools);
        var calls = 0;
        string? reply = null;
        loop.ToolInvoked += (_, _) => calls++;
        loop.ResponseReceived += (_, r) => reply = r.Text.Length > 0 ? r.Text : reply;

        await loop.RunAsync();
        return (model, calls, reply);
    }

    [Fact]
    public async Task A_calculation_costs_one_point_so_many_fit_in_a_turn()
    {
        var (_, calls, reply) = await RunAsync("calculate", points: 6);

        Assert.Equal(6, calls);
        Assert.Equal("done", reply);
    }

    [Fact]
    public async Task A_web_search_costs_three_points_so_fewer_fit()
    {
        var (_, calls, _) = await RunAsync("web_search", points: 6);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Calls_made_together_in_one_step_each_spend_their_points()
    {
        var (_, calls, _) = await RunAsync("web_search", points: 6, callsPerStep: 3);

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task The_step_limit_still_applies_when_points_are_not_spent()
    {
        var (_, calls, _) = await RunAsync("request_tools_free", points: 1000, steps: 4);

        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task The_last_step_offers_no_tools_and_ends_in_the_models_own_reply()
    {
        var (model, _, reply) = await RunAsync("web_search", points: 3);

        Assert.Empty(model.Requests.Last().Tools);
        Assert.Contains("tool budget", model.Requests.Last().EphemeralContext);
        Assert.Equal("done", reply);
    }

    [Fact]
    public void Weights_follow_what_a_call_brings_back()
    {
        Assert.Equal(0, ToolWeights.Of("request_tools"));
        Assert.True(ToolWeights.Of("calculate") < ToolWeights.Of("web_search"));
        Assert.Equal(1, ToolWeights.Of("todo_add"));
        Assert.Equal(ToolWeights.Default, ToolWeights.Of("anything_else"));
    }

    [Fact]
    public async Task A_search_snippet_is_flattened_and_cut()
    {
        var long_ = string.Join("\n", Enumerable.Repeat("word", 400));
        var tool = new WebSearchTool(new Hits(new WebSearchHit("Title", "https://x", long_)));

        var result = await tool.InvokeAsync(new ToolInput(new System.Text.Json.Nodes.JsonObject { ["query"] = "q" }));

        Assert.True(result.Content.Length < 450);
        Assert.EndsWith("...", result.Content);
        Assert.DoesNotContain("word\nword", result.Content);
    }
}
