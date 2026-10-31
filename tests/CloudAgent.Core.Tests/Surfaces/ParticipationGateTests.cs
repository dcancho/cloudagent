using System.Text.Json;
using CloudAgent.Core.Surfaces;

namespace CloudAgent.Core.Tests.Surfaces;

public sealed class ParticipationGateTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly ParticipationGate _gate;

    public ParticipationGateTests() => _gate = new ParticipationGate(new ParticipationOptions(), _time);

    private InboundMessage Say(string sender, string text, bool addressed = false, ConversationKind kind = ConversationKind.Group) =>
        new("discord", "room", kind, sender, sender, text, _time.GetUtcNow(), addressed);

    [Fact]
    public void A_direct_message_always_passes()
    {
        var message = Say("diego", "hola", kind: ConversationKind.Direct);

        Assert.Same(message, _gate.Admit(message));
    }

    [Fact]
    public void A_group_message_that_does_not_address_the_agent_gets_no_turn()
    {
        Assert.Null(_gate.Admit(Say("diego", "jose, viste el partido?")));
    }

    [Fact]
    public void An_addressed_message_gets_a_turn_with_what_was_overheard_before_it()
    {
        _gate.Admit(Say("diego", "jose, necesitamos revisar los costos"));
        _gate.Admit(Say("jose", "si, los de esta semana"));

        var admitted = _gate.Admit(Say("diego", "@agente cuanto llevamos?", addressed: true));

        Assert.NotNull(admitted);
        Assert.Equal(["jose, necesitamos revisar los costos", "si, los de esta semana"], admitted.Overheard!.Select(m => m.Text));
    }

    [Fact]
    public void What_was_overheard_is_handed_over_once()
    {
        _gate.Admit(Say("jose", "ruido"));
        _gate.Admit(Say("diego", "@agente hola", addressed: true));

        var next = _gate.Admit(Say("jose", "@agente y esto", addressed: true));

        Assert.Null(next!.Overheard);
    }

    [Fact]
    public void Someone_the_agent_is_talking_with_does_not_need_to_address_it_again()
    {
        _gate.Admit(Say("diego", "@agente hola", addressed: true));
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.NotNull(_gate.Admit(Say("diego", "y de paso dime otra cosa")));
    }

    [Fact]
    public void The_engagement_belongs_to_the_person_who_addressed_the_agent()
    {
        _gate.Admit(Say("diego", "@agente hola", addressed: true));

        Assert.Null(_gate.Admit(Say("jose", "oye diego, mira esto")));
    }

    [Fact]
    public void The_engagement_lapses()
    {
        _gate.Admit(Say("diego", "@agente hola", addressed: true));
        _time.Advance(TimeSpan.FromSeconds(91));

        Assert.Null(_gate.Admit(Say("diego", "gracias")));
    }

    [Fact]
    public void An_answer_keeps_the_engaged_people_engaged()
    {
        _gate.Admit(Say("diego", "@agente hola", addressed: true));
        _time.Advance(TimeSpan.FromSeconds(80));
        _gate.Answered("discord", "room");
        _time.Advance(TimeSpan.FromSeconds(80));

        Assert.NotNull(_gate.Admit(Say("diego", "una cosa mas")));
    }

    [Fact]
    public void An_answer_does_not_engage_people_who_never_addressed_the_agent()
    {
        _gate.Admit(Say("diego", "@agente hola", addressed: true));
        _gate.Answered("discord", "room");

        Assert.Null(_gate.Admit(Say("jose", "interesante")));
    }

    [Fact]
    public void Zero_engagement_requires_addressing_every_message()
    {
        var strict = new ParticipationGate(new ParticipationOptions { Engagement = TimeSpan.Zero }, _time);
        strict.Admit(Say("diego", "@agente hola", addressed: true));

        Assert.Null(strict.Admit(Say("diego", "y otra cosa")));
    }

    [Fact]
    public void Overheard_messages_expire_and_are_capped()
    {
        var gate = new ParticipationGate(new ParticipationOptions { MaxOverheard = 2 }, _time);
        gate.Admit(Say("jose", "viejo"));
        _time.Advance(TimeSpan.FromMinutes(31));
        gate.Admit(Say("jose", "uno"));
        gate.Admit(Say("jose", "dos"));
        gate.Admit(Say("jose", "tres"));

        var admitted = gate.Admit(Say("diego", "@agente", addressed: true));

        Assert.Equal(["dos", "tres"], admitted!.Overheard!.Select(m => m.Text));
    }

    [Fact]
    public void Conversations_do_not_share_context_or_engagement()
    {
        _gate.Admit(Say("diego", "@agente hola", addressed: true));
        var elsewhere = new InboundMessage("discord", "other", ConversationKind.Group, "diego", "diego", "hola?", _time.GetUtcNow(), false);

        Assert.Null(_gate.Admit(elsewhere));
    }

    [Fact]
    public void Overheard_lines_come_first_and_are_marked_in_the_text_the_agent_reads()
    {
        _gate.Admit(Say("jose", "los costos subieron"));
        var admitted = _gate.Admit(Say("diego", "@agente por que?", addressed: true))!;

        var text = new Burst("discord", "room", [admitted]).TextAttributed(_ => null);

        Assert.Equal("[overheard] jose: los costos subieron\ndiego: @agente por que?", text);
    }

    [Theory]
    [InlineData("""{"text":"hola a todos","chat":{"id":1,"type":"group"}}""", false)]
    [InlineData("""{"text":"oye @CloudBot dime","chat":{"id":1,"type":"group"}}""", true)]
    [InlineData("""{"text":"si","reply_to_message":{"from":{"id":42}},"chat":{"id":1,"type":"group"}}""", true)]
    [InlineData("""{"text":"si","reply_to_message":{"from":{"id":7}},"chat":{"id":1,"type":"group"}}""", false)]
    public void A_telegram_message_addresses_the_bot_by_mention_or_reply(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, TelegramSurface.IsAddressed(document.RootElement, 42, "cloudbot"));
    }

    [Fact]
    public void A_telegram_bot_of_unknown_identity_counts_every_message_as_addressed()
    {
        using var document = JsonDocument.Parse("""{"text":"hola"}""");

        Assert.True(TelegramSurface.IsAddressed(document.RootElement, null, null));
    }
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
