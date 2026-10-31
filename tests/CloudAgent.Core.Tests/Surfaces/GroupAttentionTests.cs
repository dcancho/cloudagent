using CloudAgent.Core.Surfaces;
using Xunit;

namespace CloudAgent.Core.Tests.Surfaces;

public class GroupAttentionTests
{
    private static readonly AgentIdentity Atlas = new("Atlas", ["bot"]);

    private static InboundMessage Message(string senderId, string text, Addressing addressing = Addressing.None) =>
        new("discord", "room", ConversationKind.Group, senderId, null, text, DateTimeOffset.UnixEpoch, addressing);

    private static Burst Burst(params InboundMessage[] messages) => new("discord", "room", messages);

    private static Task<GroupVerdict> Decide(GroupAttention attention, Burst burst) =>
        attention.DecideAsync(burst, burst.TextAttributed(_ => null));

    [Fact]
    public async Task Explicit_mention_is_answered_even_without_a_name()
    {
        var attention = new GroupAttention(identity: null);

        Assert.Equal(GroupVerdict.Answer, await Decide(attention, Burst(Message("1", "@Bot hola", Addressing.Explicit))));
    }

    [Fact]
    public async Task Chatter_between_people_is_held()
    {
        var attention = new GroupAttention(Atlas);

        Assert.Equal(GroupVerdict.Listen, await Decide(attention, Burst(Message("1", "¿vamos al cine?"))));
    }

    [Theory]
    [InlineData("atlas, ¿qué hora es?")]
    [InlineData("Oye ATLÁS")]
    [InlineData("pregúntale al bot")]
    public async Task Name_without_judge_is_answered(string text)
    {
        var attention = new GroupAttention(Atlas);

        Assert.Equal(GroupVerdict.Answer, await Decide(attention, Burst(Message("1", text))));
    }

    [Theory]
    [InlineData("el atlas de geografía")]
    [InlineData("atlases")]
    [InlineData("robot")]
    public void Name_inside_a_word_does_not_count_but_a_whole_word_does(string text)
    {
        Assert.Equal(text == "el atlas de geografía", Atlas.IsNamedIn(text));
    }

    [Fact]
    public async Task Judge_decides_name_only_messages()
    {
        string? seen = null;
        var attention = new GroupAttention(Atlas, (situation, _) =>
        {
            seen = situation;
            return Task.FromResult(false);
        });
        attention.Hold("discord", "room", "1: ayer usé a Atlas");

        Assert.Equal(GroupVerdict.Listen, await Decide(attention, Burst(Message("2", "Atlas se equivocó"))));
        Assert.Contains("ayer usé a Atlas", seen);
        Assert.Contains("2: Atlas se equivocó", seen);
    }

    [Fact]
    public async Task Judge_failure_answers()
    {
        var attention = new GroupAttention(Atlas, (_, _) => throw new InvalidOperationException("down"));

        Assert.Equal(GroupVerdict.Answer, await Decide(attention, Burst(Message("1", "Atlas?"))));
    }

    [Fact]
    public async Task Follow_up_from_the_person_answered_counts_until_the_window_closes()
    {
        var time = new FakeTimeProvider();
        var attention = new GroupAttention(Atlas, options: new GroupAttentionOptions { FollowUp = TimeSpan.FromMinutes(2) }, time: time);
        attention.Answered("discord", "room", ["1"]);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(GroupVerdict.Answer, await Decide(attention, Burst(Message("1", "¿y cuánto cuesta?"))));
        Assert.Equal(GroupVerdict.Listen, await Decide(attention, Burst(Message("2", "¿y cuánto cuesta?"))));

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(GroupVerdict.Listen, await Decide(attention, Burst(Message("1", "¿y cuánto cuesta?"))));
    }

    [Fact]
    public void Held_messages_lead_the_next_turn_once()
    {
        var attention = new GroupAttention(Atlas);
        attention.Hold("discord", "room", "1: hola\n2: qué tal");

        Assert.Equal(
            "[Earlier in the group, not addressed to you]\n1: hola\n2: qué tal\n\n[New]\n1: Atlas, resume",
            attention.Compose("discord", "room", "1: Atlas, resume"));
        Assert.Equal("1: otra", attention.Compose("discord", "room", "1: otra"));
    }

    [Fact]
    public void Held_messages_keep_only_the_latest()
    {
        var attention = new GroupAttention(Atlas, options: new GroupAttentionOptions { MaxHeld = 2 });
        attention.Hold("discord", "room", "a\nb");
        attention.Hold("discord", "room", "c");

        Assert.StartsWith("[Earlier in the group, not addressed to you]\nb\nc\n", attention.Compose("discord", "room", "d"));
    }

    [Theory]
    [InlineData("<silent/>", "")]
    [InlineData("  <SILENT/>\n", "")]
    [InlineData("Claro. <silent/>", "Claro.")]
    [InlineData("Hola", "Hola")]
    public void Silent_marker_is_removed(string reply, string sent)
    {
        Assert.Equal(sent, SilentReply.Strip(reply));
    }
}

internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
