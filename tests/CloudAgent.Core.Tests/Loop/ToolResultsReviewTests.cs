using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Loop;
using CloudAgent.Core.Providers;
using CloudAgent.Core.Tests.Support;
using CloudAgent.Core.Tools;
using CloudAgent.Core.Tools.Results;

namespace CloudAgent.Core.Tests.Loop;

public sealed class ToolResultsReviewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloudagent-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class Stub(string name, string content) : ITool
    {
        public string Name => name;
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
    public async Task A_batch_that_would_overspend_runs_only_what_the_points_cover_and_answers_the_rest()
    {
        var model = new ScriptedLlmProvider()
            .When(r => r.Tools.Count > 0 && r.Messages[^1].Role == Role.User, _ => new LlmResponse("")
            {
                ToolCalls = [new ToolCall("a", "web_search", "{}"), new ToolCall("b", "web_search", "{}"), new ToolCall("c", "web_search", "{}")],
            })
            .Otherwise("done");
        var context = new AgentContext();
        context.AddUser("go");
        var executed = 0;
        var loop = new AgentLoop(
            model,
            new AgentOptions { MaxIterations = 1, IterationDelay = TimeSpan.Zero, MaxToolPoints = 6 },
            context: context,
            tools: new ToolRegistry().Register(new Stub("web_search", "result")));
        loop.ToolInvoked += (_, _) => executed++;

        await loop.RunAsync();

        var results = context.Messages.Where(m => m.Role == Role.Tool).ToList();
        Assert.Equal(2, executed);
        Assert.Equal(["a", "b", "c"], results.Select(m => m.ToolCallId));
        Assert.Contains("not run", results[2].Content);
    }

    [Fact]
    public async Task A_result_store_that_cannot_write_falls_back_to_cutting_the_result_and_the_turn_goes_on()
    {
        Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "file");
        File.WriteAllText(blocked, "not a directory");
        var model = new ScriptedLlmProvider()
            .When(r => r.Tools.Count > 0 && r.Messages[^1].Role == Role.User, _ => new LlmResponse("")
            {
                ToolCalls = [new ToolCall("c1", "big", "{}")],
            })
            .Otherwise("done");
        var context = new AgentContext();
        context.AddUser("go");
        string? reply = null;
        var loop = new AgentLoop(
            model,
            new AgentOptions { MaxIterations = 1, IterationDelay = TimeSpan.Zero, MaxInlineResultChars = 100 },
            context: context,
            tools: new ToolRegistry().Register(new Stub("big", new string('x', 500)))) { Results = new ToolResultStore(blocked) };
        loop.ResponseReceived += (_, r) => reply = r.Text.Length > 0 ? r.Text : reply;

        await loop.RunAsync();

        Assert.Equal("done", reply);
        Assert.Equal(500, context.Messages.Single(m => m.Role == Role.Tool).Content.Length);
    }

    [Fact]
    public void Shrinking_an_older_result_keeps_the_way_to_read_the_rest_from_where_it_now_ends()
    {
        var store = new ToolResultStore(_directory);
        var full = new string('a', 6000);
        var id = store.Save(full);
        var cut = full[..3000] + ToolResultStore.Footer(full.Length, 3000, id);
        var messages = new List<Message> { Message.User("go"), Message.Assistant("", [new ToolCall("c1", "big", "{}")]), Message.ToolResult("c1", "big", cut) };
        for (var i = 0; i < 4; i++)
            messages.Add(Message.User("filler"));

        var fitted = InputCap.Fit(new LlmRequest { System = "s", Messages = messages, Model = "m" }, 500).Request;

        var shrunk = fitted.Messages.Single(m => m.Role == Role.Tool).Content;
        Assert.True(shrunk.Length < 700);
        Assert.Contains($"id \"{id}\"", shrunk);
        Assert.Contains("offset 400", shrunk);
        Assert.Equal(full[400..450], store.Read(id, 400, 50));
    }

    [Fact]
    public void A_result_already_shrunk_is_not_shrunk_again()
    {
        var store = new ToolResultStore(_directory);
        var id = store.Save(new string('a', 6000));
        var once = new string('a', 400) + ToolResultStore.CutFooter(id, 400);
        var messages = new List<Message> { Message.User("go"), Message.ToolResult("c1", "big", once) };
        for (var i = 0; i < 4; i++)
            messages.Add(Message.User("filler"));

        var fitted = InputCap.Fit(new LlmRequest { System = "s", Messages = messages, Model = "m" }, 50).Request;

        Assert.Equal(once, fitted.Messages.Single(m => m.Role == Role.Tool).Content);
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(99999, 100)]
    public async Task An_offset_outside_the_result_is_brought_inside_before_the_marker_is_written(int offset, int expected)
    {
        var store = new ToolResultStore(_directory);
        var id = store.Save(new string('a', 100));

        var result = await new ReadResultTool(store).InvokeAsync(Args(("id", id), ("offset", offset), ("length", 10)));

        Assert.Contains($"characters {expected} to", result.Content);
    }

    [Theory]
    [InlineData("""{"usage":{"prompt_tokens":10,"prompt_tokens_details":{"cached_tokens":8}}}""", 8)]
    [InlineData("""{"usage":{"prompt_tokens":10,"prompt_tokens_details":null}}""", null)]
    [InlineData("""{"usage":{"prompt_tokens_details":{"cached_tokens":null}}}""", null)]
    [InlineData("""{"usage":null}""", null)]
    [InlineData("""[1,2]""", null)]
    [InlineData("not json", null)]
    public void Cached_tokens_are_read_when_present_and_missing_metadata_is_never_an_error(string body, int? expected)
    {
        Assert.Equal(expected, FoundryLlmProvider.CachedTokens(BinaryData.FromString(body)));
    }

    private sealed class Reply(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task Anthropic_input_includes_cache_reads_and_writes_so_cached_is_always_part_of_it()
    {
        var body = """{"content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn","usage":{"input_tokens":1000,"output_tokens":5,"cache_read_input_tokens":90000,"cache_creation_input_tokens":2000}}""";
        var provider = new AnthropicLlmProvider("key", new HttpClient(new Reply(body)));

        var response = await provider.CompleteAsync(new LlmRequest { Messages = [Message.User("hi")], Model = "m" });

        Assert.Equal(93000, response.InputTokens);
        Assert.Equal(90000, response.CacheReadTokens);
    }

    [Fact]
    public void Cached_cost_is_right_for_a_provider_whose_input_includes_the_cache()
    {
        var record = new UsageRecord(DateTimeOffset.UtcNow, "turn", "m", null, 91_000, 0, 90_000, null, TimeSpan.Zero, false);

        var report = UsageReport.Build([record], ModelPrices.Parse("m=1/10/0.1"));

        Assert.Equal(0.01m, report.Total.Cost);
        Assert.Equal(90_000, report.Total.CachedTokens);
    }
}
