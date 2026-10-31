using CloudAgent.Core.Surfaces;
using Xunit;

namespace CloudAgent.Core.Tests.Surfaces;

public class BurstAttributionTests
{
    private static InboundMessage Message(ConversationKind kind, string senderId, string? display, string text) =>
        new("discord", "room", kind, senderId, display, text, DateTimeOffset.UnixEpoch);

    private static string? KnownPeople(InboundMessage message) => message.SenderId == "123" ? "diego" : null;

    [Fact]
    public void Group_message_is_labelled_with_the_person()
    {
        var burst = new Burst("discord", "room", [Message(ConversationKind.Group, "123", "Diego C", "hola")]);

        Assert.Equal("diego: hola", burst.TextAttributed(KnownPeople));
    }

    [Fact]
    public void Unknown_sender_falls_back_to_display_name_then_id()
    {
        var burst = new Burst("discord", "room",
        [
            Message(ConversationKind.Group, "999", "Pepe", "hi"),
            Message(ConversationKind.Group, "888", null, "hey"),
        ]);

        Assert.Equal("Pepe: hi\n888: hey", burst.TextAttributed(KnownPeople));
    }

    [Fact]
    public void Burst_from_several_people_labels_each_message()
    {
        var burst = new Burst("discord", "room",
        [
            Message(ConversationKind.Group, "123", null, "one"),
            Message(ConversationKind.Group, "999", "Pepe", "two"),
            Message(ConversationKind.Group, "123", null, "three"),
        ]);

        Assert.Equal("diego: one\nPepe: two\ndiego: three", burst.TextAttributed(KnownPeople));
    }

    [Fact]
    public void Direct_message_is_not_labelled()
    {
        var burst = new Burst("discord", "room", [Message(ConversationKind.Direct, "123", "Diego C", "hola")]);

        Assert.Equal("hola", burst.TextAttributed(KnownPeople));
    }
}
