using System.Text.Json.Nodes;
using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Attention;
using CloudAgent.Core.Providers;
using CloudAgent.Core.Tests.Support;

namespace CloudAgent.Core.Tests.Providers;

public sealed class PromptCompositionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloudagent-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Each_part_of_a_request_is_measured_apart()
    {
        var request = new LlmRequest
        {
            System = AttentionRendering.System("persona", []) + AttentionRendering.StandingHeader + "- fact",
            EphemeralContext = $"{AttentionRendering.StateHeader} (x)\nstate\n\n{AttentionRendering.SummaryHeader}\nsummary text\n\n{AttentionRendering.RecalledHeader}\n- memory",
            Tools = [new ToolDefinition("calc", "adds", new JsonObject())],
            Messages =
            [
                Message.User("hello"),
                Message.Assistant("ok", [new ToolCall("1", "calc", "{}")]),
                Message.ToolResult("1", "calc", "42"),
            ],
        };

        var chars = PromptComposition.Measure(request);

        Assert.Equal(7, chars[PromptPart.Persona]);
        Assert.Equal(AttentionRendering.StandingHeader.Length + 6, chars[PromptPart.Standing]);
        Assert.True(chars[PromptPart.State] > 0);
        Assert.True(chars[PromptPart.Summary] > chars[PromptPart.State]);
        Assert.True(chars[PromptPart.Recalled] > 0);
        Assert.False(chars.ContainsKey(PromptPart.Stubs));
        Assert.Equal(5, chars[PromptPart.User]);
        Assert.Equal(2 + 4 + 2, chars[PromptPart.Assistant]);
        Assert.Equal(2, chars[PromptPart.ToolResults]);
        Assert.True(chars[PromptPart.Tools] > 0);
    }

    [Fact]
    public void Ephemeral_text_before_any_section_is_counted_as_other()
    {
        var chars = PromptComposition.Measure(new LlmRequest { EphemeralContext = "loose text" });

        Assert.Equal(10, chars[PromptPart.Other]);
    }

    [Fact]
    public async Task The_ledger_keeps_the_composition_of_each_call()
    {
        var ledger = new UsageLedger(Path.Combine(_directory, "usage.jsonl"));
        var provider = new LedgeredLlmProvider(new ScriptedLlmProvider().Otherwise("ok"), ledger);

        await provider.CompleteAsync(new LlmRequest { System = "persona", Messages = [Message.User("hello")] });

        var chars = Assert.Single(ledger.Read()).Chars!;
        Assert.Equal(7, chars[PromptPart.Persona]);
        Assert.Equal(5, chars[PromptPart.User]);
    }

    [Fact]
    public void Input_tokens_are_divided_among_parts_by_their_share_of_the_characters()
    {
        UsageRecord Call(int input, params (string Part, int Size)[] parts) =>
            new(DateTimeOffset.UtcNow, LlmPurpose.Turn, "m", null, input, 1, null, null, TimeSpan.Zero, false,
                parts.ToDictionary(p => p.Part, p => p.Size));

        var report = UsageReport.Build(
        [
            Call(1000, (PromptPart.Persona, 100), (PromptPart.Tools, 300)),
            Call(400, (PromptPart.Persona, 100), (PromptPart.User, 100)),
            new UsageRecord(DateTimeOffset.UtcNow, LlmPurpose.Turn, "m", null, 999, 1, null, null, TimeSpan.Zero, false),
        ], new Dictionary<string, ModelPrice>());

        Assert.Equal(
            [(PromptPart.Tools, 750L), (PromptPart.Persona, 450L), (PromptPart.User, 200L)],
            report.ByPart.Select(p => (p.Part, p.Tokens)));
        Assert.Equal(1.0, report.ByPart.Sum(p => p.Share), 6);
    }
}
