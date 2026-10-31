using System.Text.Json;
using CloudAgent.Core.Surfaces;
using Xunit;

namespace CloudAgent.Core.Tests.Surfaces;

public class TelegramAddressingTests
{
    private static readonly TelegramSurface.TelegramBot Bot = new(77, "atlas_bot");

    private static Addressing Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var message = document.RootElement;
        return TelegramSurface.ReadAddressing(message, message.GetProperty("text").GetString()!, Bot);
    }

    [Fact]
    public void Mention_of_the_bot_is_explicit()
    {
        Assert.Equal(Addressing.Explicit, Read("""{ "text": "hola @Atlas_Bot, ¿qué tal?" }"""));
    }

    [Fact]
    public void Mention_of_a_longer_username_is_not()
    {
        Assert.Equal(Addressing.None, Read("""{ "text": "hola @atlas_bot2" }"""));
    }

    [Fact]
    public void Reply_to_the_bot_is_explicit()
    {
        Assert.Equal(Addressing.Explicit, Read("""{ "text": "¿y eso?", "reply_to_message": { "message_id": 5, "from": { "id": 77 } } }"""));
    }

    [Fact]
    public void Reply_to_someone_else_is_not()
    {
        Assert.Equal(Addressing.None, Read("""{ "text": "¿y eso?", "reply_to_message": { "message_id": 5, "from": { "id": 12 } } }"""));
    }

    [Fact]
    public void Topic_starter_written_by_the_bot_does_not_count()
    {
        var json = """
            { "text": "hola", "is_topic_message": true, "message_thread_id": 9,
              "reply_to_message": { "message_id": 9, "from": { "id": 77 } } }
            """;

        Assert.Equal(Addressing.None, Read(json));
    }

    [Fact]
    public void Bot_username_reads_as_the_agent_name()
    {
        Assert.Equal("hola @Atlas, y @atlas_bot2", TelegramSurface.NameTheBot("hola @atlas_bot, y @atlas_bot2", "atlas_bot", "Atlas"));
    }
}
