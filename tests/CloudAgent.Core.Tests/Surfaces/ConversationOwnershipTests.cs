using CloudAgent.Core.Surfaces;
using Xunit;

namespace CloudAgent.Core.Tests.Surfaces;

public sealed class ConversationOwnershipTests
{
    [Fact]
    public void A_conversation_belongs_only_to_the_sender_who_opened_it()
    {
        var id = ConversationOwnership.NewId("alice@example.com");

        Assert.True(ConversationOwnership.Owns("alice@example.com", id));
        Assert.False(ConversationOwnership.Owns("bob@example.com", id));
        Assert.False(ConversationOwnership.Owns("local", id));
    }

    [Fact]
    public void An_id_without_an_owner_belongs_to_nobody()
    {
        Assert.False(ConversationOwnership.Owns("alice@example.com", "1dff3f95fe3a"));
    }

    [Fact]
    public void New_ids_are_lowercase_alphanumeric()
    {
        var id = ConversationOwnership.NewId("alice@example.com");

        Assert.All(id, c => Assert.True(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)));
        Assert.NotEqual(id, ConversationOwnership.NewId("alice@example.com"));
    }
}
