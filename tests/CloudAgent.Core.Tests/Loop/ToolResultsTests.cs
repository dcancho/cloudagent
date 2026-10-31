using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Loop;
using CloudAgent.Core.Providers;
using CloudAgent.Core.Tests.Support;
using CloudAgent.Core.Tools;
using CloudAgent.Core.Tools.Results;

namespace CloudAgent.Core.Tests.Loop;

public sealed class ToolResultsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloudagent-tests", Guid.NewGuid().ToString("N"));
    private readonly ToolResultStore _store;

    public ToolResultsTests() => _store = new ToolResultStore(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class BigTool(string content) : ITool
    {
        public string Name => "big";
        public string Description => "stub";
        public IReadOnlyList<ToolParameter> Parameters => [];
        public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Ok(content));
    }

    private static ToolInput Args(params (string Key, object Value)[] pairs)
    {
        var json = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in pairs)
            json[key] = System.Text.Json.Nodes.JsonValue.Create(value);
        return new ToolInput(json);
    }

    [Fact]
    public void A_saved_result_is_read_back_in_slices()
    {
        var id = _store.Save("abcdefghij");

        Assert.Equal("cde", _store.Read(id, 2, 3));
        Assert.Equal("hij", _store.Read(id, 7, 100));
        Assert.Equal(10, _store.LengthOf(id));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("zzzzzzzzzzzz")]
    [InlineData("")]
    public void An_id_that_is_not_one_the_store_issued_reads_nothing(string id)
    {
        Assert.Null(_store.Read(id, 0, 10));
        Assert.Null(_store.LengthOf(id));
    }

    [Fact]
    public void Old_results_are_pruned_and_recent_ones_stay()
    {
        var old = _store.Save("old");
        var recent = _store.Save("recent");
        File.SetLastWriteTimeUtc(Path.Combine(_directory, old + ".txt"), DateTime.UtcNow.AddDays(-10));

        Assert.Equal(1, _store.Prune(DateTimeOffset.UtcNow.AddDays(-7)));

        Assert.Null(_store.Read(old, 0, 10));
        Assert.Equal("recent", _store.Read(recent, 0, 10));
    }

    [Fact]
    public async Task The_tool_continues_where_the_cut_result_stopped_and_says_when_it_ends()
    {
        var id = _store.Save(new string('a', 5000));
        var tool = new ReadResultTool(_store);

        var first = await tool.InvokeAsync(Args(("id", id), ("offset", 0), ("length", 3000)));
        var last = await tool.InvokeAsync(Args(("id", id), ("offset", 3000), ("length", 3000)));

        Assert.Contains("continue with offset 3000", first.Content);
        Assert.Contains("end of result", last.Content);
        Assert.Contains("characters 3000 to 5000 of 5000", last.Content);
    }

    [Fact]
    public async Task The_tool_refuses_an_unknown_id()
    {
        var result = await new ReadResultTool(_store).InvokeAsync(Args(("id", "000000000000")));

        Assert.False(result.Success);
    }

    private async Task<(AgentContext Context, ScriptedLlmProvider Model)> RunAsync(string toolContent, int inline = 3000)
    {
        var model = new ScriptedLlmProvider()
            .When(r => r.Tools.Count > 0 && r.Messages[^1].Role == Role.User, _ => new LlmResponse("")
            {
                ToolCalls = [new ToolCall("c1", "big", "{}")],
            })
            .Otherwise("done");
        var context = new AgentContext();
        context.AddUser("go");
        var tools = new ToolRegistry().Register(new BigTool(toolContent)).Register(new ReadResultTool(_store));
        var loop = new AgentLoop(
            model,
            new AgentOptions { MaxIterations = 1, IterationDelay = TimeSpan.Zero, MaxInlineResultChars = inline },
            context: context,
            tools: tools) { Results = _store };

        await loop.RunAsync();
        return (context, model);
    }

    [Fact]
    public async Task A_long_result_enters_the_context_as_its_start_with_an_id_to_read_the_rest()
    {
        var content = string.Join("\n", Enumerable.Range(1, 1000).Select(i => $"line {i} " + new string('x', 20)));

        var (context, _) = await RunAsync(content);

        var inContext = context.Messages.Single(m => m.Role == Role.Tool).Content;
        Assert.True(inContext.Length < 3300);
        Assert.StartsWith("line 1 ", inContext);
        Assert.Contains("read_result", inContext);
        var id = inContext.Split('"')[1];
        Assert.Equal(content.Length, _store.LengthOf(id));
        var shown = int.Parse(inContext.Split("offset ")[1].TrimEnd(']', '.'));
        Assert.Equal(content[shown..(shown + 50)], _store.Read(id, shown, 50));
    }

    [Fact]
    public async Task A_short_result_goes_in_whole_and_nothing_is_stored()
    {
        var (context, _) = await RunAsync("short");

        Assert.Equal("short", context.Messages.Single(m => m.Role == Role.Tool).Content);
        Assert.False(Directory.Exists(_directory) && Directory.EnumerateFiles(_directory).Any());
    }

    [Fact]
    public async Task A_result_read_back_is_not_cut_again()
    {
        var id = _store.Save(new string('a', 8000));
        var tool = new ReadResultTool(_store);

        var result = await tool.InvokeAsync(Args(("id", id), ("length", 8000)));

        Assert.True(result.Content.Length > 7999);
    }
}

public sealed class CachedPriceTests
{
    private static UsageRecord Call(int input, int output, int? cached) =>
        new(DateTimeOffset.UtcNow, "turn", "m", null, input, output, cached, null, TimeSpan.Zero, false);

    [Fact]
    public void A_third_rate_is_the_price_of_cached_input()
    {
        var price = ModelPrices.Parse("m=0.10/0.50/0.01")["m"];

        Assert.Equal(new ModelPrice(0.10m, 0.50m, 0.01m), price);
    }

    [Fact]
    public void Cached_input_is_charged_at_the_cached_rate()
    {
        var prices = ModelPrices.Parse("m=1/10/0.1");

        var report = UsageReport.Build([Call(input: 1_000_000, output: 0, cached: 800_000)], prices);

        Assert.Equal(0.28m, report.Total.Cost);
        Assert.Equal(800_000, report.Total.CachedTokens);
    }

    [Fact]
    public void Without_a_cached_rate_cached_input_costs_the_normal_rate()
    {
        var report = UsageReport.Build([Call(1_000_000, 0, 800_000)], ModelPrices.Parse("m=1/10"));

        Assert.Equal(1m, report.Total.Cost);
    }

    [Fact]
    public void A_malformed_third_rate_skips_the_price()
    {
        Assert.Empty(ModelPrices.Parse("m=1/10/x"));
    }
}
