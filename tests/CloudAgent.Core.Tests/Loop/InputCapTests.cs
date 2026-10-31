using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Attention;
using CloudAgent.Core.Loop;

namespace CloudAgent.Core.Tests.Loop;

public sealed class InputCapTests
{
    private static string Block(int chars) => new('x', chars);

    private static string Ephemeral(int summary = 0, int memories = 0, int memoryChars = 400, int stubs = 0) =>
        string.Join(
            "\n\n",
            new[]
            {
                $"{AttentionRendering.StateHeader} (x)\nstate",
                summary > 0 ? $"{AttentionRendering.SummaryHeader}\n(note)\n{Block(summary)}" : null,
                memories > 0
                    ? $"{AttentionRendering.RecalledHeader}\n(note)\n" + string.Join("\n", Enumerable.Range(1, memories).Select(i => $"- (s/k) memory{i} {Block(memoryChars)}"))
                    : null,
                stubs > 0 ? $"{AttentionRendering.StubsHeader}\n(note)\n- [id] {Block(stubs)}" : null,
            }.Where(s => s is not null));

    private static LlmRequest Request(string? ephemeral, params Message[] messages) =>
        new() { System = "persona", EphemeralContext = ephemeral, Messages = messages, Model = "m" };

    [Fact]
    public void A_request_under_the_cap_is_untouched()
    {
        var request = Request(Ephemeral(summary: 100), Message.User("hi"));

        var fitted = InputCap.Fit(request, 10_000);

        Assert.Equal(request.EphemeralContext, fitted.EphemeralContext);
        Assert.Null(fitted.Trimmed);
    }

    [Fact]
    public void A_cap_of_zero_means_no_cap()
    {
        var request = Request(Ephemeral(summary: 100_000), Message.User("hi"));

        Assert.Null(InputCap.Fit(request, 0).Trimmed);
    }

    [Fact]
    public void Stubs_go_first()
    {
        var fitted = InputCap.Fit(Request(Ephemeral(summary: 200, memories: 2, stubs: 4000), Message.User("hi")), 500);

        Assert.Equal("stubs", fitted.Trimmed);
        Assert.DoesNotContain(AttentionRendering.StubsHeader, fitted.EphemeralContext);
        Assert.Contains("memory2", fitted.EphemeralContext);
    }

    [Fact]
    public void Recalled_memories_are_dropped_from_the_least_relevant_up_until_it_fits()
    {
        var fitted = InputCap.Fit(Request(Ephemeral(memories: 5, memoryChars: 400), Message.User("hi")), 350);

        Assert.Contains("memory1", fitted.EphemeralContext);
        Assert.DoesNotContain("memory5", fitted.EphemeralContext);
        Assert.Contains("recalled memories", fitted.Trimmed);
    }

    [Fact]
    public void The_summary_goes_after_the_memories()
    {
        var fitted = InputCap.Fit(Request(Ephemeral(summary: 20_000, memories: 1), Message.User("hi")), 300);

        Assert.DoesNotContain(AttentionRendering.SummaryHeader, fitted.EphemeralContext);
        Assert.Contains("1 recalled memory", fitted.Trimmed);
        Assert.Contains("summary", fitted.Trimmed);
    }

    [Fact]
    public void Old_tool_results_are_cut_but_the_latest_messages_and_the_pairing_stay()
    {
        var messages = new List<Message> { Message.User("go") };
        for (var i = 0; i < 3; i++)
        {
            messages.Add(Message.Assistant("", [new ToolCall($"c{i}", "t", "{}")]));
            messages.Add(Message.ToolResult($"c{i}", "t", Block(8000)));
        }

        var fitted = InputCap.Fit(Request(null, messages.ToArray()), 3000);

        Assert.Equal(messages.Count, fitted.Messages.Count);
        Assert.Contains("tool result", fitted.Trimmed);
        Assert.True(fitted.Messages[2].Content.Length < 1000);
        Assert.Equal(8000, fitted.Messages[^1].Content.Length);
        Assert.Equal("c2", fitted.Messages[^1].ToolCallId);
    }

    [Fact]
    public void The_conversation_is_never_cut_even_when_it_cannot_fit()
    {
        var fitted = InputCap.Fit(Request(null, Message.User(Block(20_000))), 100);

        Assert.Equal(20_000, fitted.Messages[0].Content.Length);
        Assert.Equal("still over the cap", fitted.Trimmed);
    }

    [Fact]
    public void The_notice_is_kept_whatever_was_trimmed()
    {
        var fitted = InputCap.Fit(Request(Ephemeral(stubs: 4000), Message.User("hi")), 300, "NOTICE");

        Assert.EndsWith("NOTICE", fitted.EphemeralContext);
    }
}
