using System.Text.Json;
using CloudAgent.Core.Surfaces;
using Xunit;

namespace CloudAgent.Core.Tests.Surfaces;

public class TelegramConversationTests
{
    private static (string, ConversationKind) Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return TelegramSurface.ReadConversation(document.RootElement);
    }

    [Fact]
    public void Private_chat_is_direct()
    {
        Assert.Equal(("42", ConversationKind.Direct), Read("""{ "chat": { "id": 42, "type": "private" } }"""));
    }

    [Theory]
    [InlineData("group")]
    [InlineData("supergroup")]
    public void Group_chats_are_shared(string type)
    {
        Assert.Equal(
            ("-1001", ConversationKind.Group),
            Read($$"""{ "chat": { "id": -1001, "type": "{{type}}" } }"""));
    }

    [Fact]
    public void Forum_topic_is_its_own_conversation()
    {
        var json = """
            { "chat": { "id": -1001, "type": "supergroup" }, "is_topic_message": true, "message_thread_id": 7 }
            """;

        Assert.Equal(("-1001#7", ConversationKind.Group), Read(json));
    }

    [Fact]
    public void Reply_thread_outside_a_forum_stays_in_the_chat()
    {
        var json = """{ "chat": { "id": -1001, "type": "supergroup" }, "message_thread_id": 7 }""";

        Assert.Equal(("-1001", ConversationKind.Group), Read(json));
    }
}
