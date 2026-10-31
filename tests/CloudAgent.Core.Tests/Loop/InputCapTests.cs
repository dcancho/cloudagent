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

    private static LlmRequest Fit(LlmRequest request, int cap, string? notice = null, int memories = 0) =>
        InputCap.Fit(request, cap, notice, memories).Request;

    private static LlmRequest Request(string? ephemeral, params Message[] messages) =>
        new() { System = "persona", EphemeralContext = ephemeral, Messages = messages, Model = "m" };

    [Fact]
    public void A_request_under_the_cap_is_untouched()
    {
        var request = Request(Ephemeral(summary: 100), Message.User("hi"));

        var fitted = Fit(request, 10_000);

        Assert.Equal(request.EphemeralContext, fitted.EphemeralContext);
        Assert.Null(fitted.Trimmed);
    }

    [Fact]
    public void A_cap_of_zero_means_no_cap()
    {
        var request = Request(Ephemeral(summary: 100_000), Message.User("hi"));

        Assert.Null(Fit(request, 0).Trimmed);
    }

    [Fact]
    public void Stubs_go_first()
    {
        var fitted = Fit(Request(Ephemeral(summary: 200, memories: 2, stubs: 4000), Message.User("hi")), 500, memories: 2);

        Assert.Equal("stubs", fitted.Trimmed);
        Assert.DoesNotContain(AttentionRendering.StubsHeader, fitted.EphemeralContext);
        Assert.Contains("memory2", fitted.EphemeralContext);
    }

    [Fact]
    public void Recalled_memories_are_dropped_from_the_least_relevant_up_until_it_fits()
    {
        var fitted = Fit(Request(Ephemeral(memories: 5, memoryChars: 400), Message.User("hi")), 350, memories: 5);

        Assert.Contains("memory1", fitted.EphemeralContext);
        Assert.DoesNotContain("memory5", fitted.EphemeralContext);
        Assert.Contains("recalled memories", fitted.Trimmed);
    }

    [Fact]
    public void The_summary_goes_after_the_memories()
    {
        var fitted = Fit(Request(Ephemeral(summary: 20_000, memories: 1), Message.User("hi")), 300, memories: 1);

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

        var fitted = Fit(Request(null, messages.ToArray()), 3000);

        Assert.Equal(messages.Count, fitted.Messages.Count);
        Assert.Contains("tool result", fitted.Trimmed);
        Assert.True(fitted.Messages[2].Content.Length < 1000);
        Assert.Equal(8000, fitted.Messages[^1].Content.Length);
        Assert.Equal("c2", fitted.Messages[^1].ToolCallId);
    }

    [Fact]
    public void The_conversation_is_never_cut_even_when_it_cannot_fit()
    {
        var fitted = Fit(Request(null, Message.User(Block(20_000))), 100);

        Assert.Equal(20_000, fitted.Messages[0].Content.Length);
        Assert.Equal("still over the cap", fitted.Trimmed);
    }

    [Fact]
    public void The_notice_is_kept_whatever_was_trimmed()
    {
        var fitted = Fit(Request(Ephemeral(stubs: 4000), Message.User("hi")), 300, "NOTICE");

        Assert.EndsWith("NOTICE", fitted.EphemeralContext);
    }

    [Fact]
    public void A_memory_with_its_own_bullets_is_dropped_whole()
    {
        var multiline = $"{AttentionRendering.RecalledHeader}\n(note)\n- (s/k) first {Block(400)}\n- (s/k) second\n- a bullet inside it\n- another {Block(400)}";
        var request = Request($"{AttentionRendering.StateHeader} (x)\nstate\n\n{multiline}", Message.User("hi"));

        var fit = InputCap.Fit(request, 150, recalledMemories: 2);

        Assert.Equal(1, fit.RecalledKept);
        Assert.Contains("first", fit.Request.EphemeralContext);
        Assert.DoesNotContain("a bullet inside it", fit.Request.EphemeralContext);
        Assert.DoesNotContain("another", fit.Request.EphemeralContext);
    }

    [Fact]
    public void A_recalled_section_that_does_not_parse_into_the_expected_memories_is_dropped_whole()
    {
        var fit = InputCap.Fit(Request(Ephemeral(memories: 2, memoryChars: 400), Message.User("hi")), 150, recalledMemories: 5);

        Assert.Equal(0, fit.RecalledKept);
        Assert.DoesNotContain(AttentionRendering.RecalledHeader, fit.Request.EphemeralContext);
    }

    [Fact]
    public void The_memories_kept_are_reported_so_only_those_count_as_injected()
    {
        var fit = InputCap.Fit(Request(Ephemeral(memories: 5, memoryChars: 400), Message.User("hi")), 350, recalledMemories: 5);

        Assert.InRange(fit.RecalledKept, 1, 4);
        Assert.Equal(fit.RecalledKept, Enumerable.Range(1, 5).Count(i => fit.Request.EphemeralContext!.Contains($"memory{i} ")));
    }

    [Fact]
    public void Turn_guidance_survives_when_the_section_before_it_is_dropped()
    {
        var guidance = $"{AttentionRendering.GuidanceHeader}\n- compute every figure";
        var request = Request($"{Ephemeral(summary: 20_000)}\n\n{guidance}", Message.User("hi"));

        var fitted = Fit(request, 300);

        Assert.DoesNotContain(AttentionRendering.SummaryHeader, fitted.EphemeralContext);
        Assert.Contains("compute every figure", fitted.EphemeralContext);
    }

    [Fact]
    public void A_heading_inside_the_summary_does_not_split_it()
    {
        var summary = $"{AttentionRendering.SummaryHeader}\n(note)\nintro\n\n## Decisions\nbody {Block(4000)}";
        var request = Request($"{AttentionRendering.StateHeader} (x)\nstate\n\n{summary}", Message.User("hi"));

        var fitted = Fit(request, 300);

        Assert.DoesNotContain("body", fitted.EphemeralContext);
        Assert.DoesNotContain("## Decisions", fitted.EphemeralContext);
    }

    [Fact]
    public void A_tool_result_barely_over_the_kept_length_is_left_alone()
    {
        var messages = new List<Message> { Message.User("go") };
        for (var i = 0; i < 3; i++)
        {
            messages.Add(Message.Assistant("", [new ToolCall($"c{i}", "t", "{}")]));
            messages.Add(Message.ToolResult($"c{i}", "t", Block(420)));
        }

        var fitted = Fit(Request(null, messages.ToArray()), 100);

        Assert.All(fitted.Messages.Where(m => m.Role == Role.Tool), m => Assert.Equal(420, m.Content.Length));
    }
}
