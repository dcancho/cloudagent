using CloudAgent.Core.Surfaces;

namespace CloudAgent.Core.Tests.Surfaces;

public sealed class AgentIdentityTests
{
    [Fact]
    public void The_configured_name_wins_then_the_handle_then_the_default()
    {
        Assert.Equal("Luna", AgentIdentity.NameOf(" Luna ", "@CloudAgentBeta"));
        Assert.Equal("CloudAgentBeta", AgentIdentity.NameOf(null, "@CloudAgentBeta"));
        Assert.Equal(AgentIdentity.DefaultName, AgentIdentity.NameOf("", null));
    }

    [Fact]
    public void A_direct_conversation_names_the_person_and_says_nobody_else_reads_it()
    {
        var text = AgentIdentity.Describe("CloudAgentBeta", "@CloudAgentBeta", "discord", ConversationKind.Direct, "jose");

        Assert.Contains("Your name is CloudAgentBeta.", text);
        Assert.Contains("direct conversation on discord with jose", text);
        Assert.DoesNotContain("@CloudAgentBeta", text);
        Assert.DoesNotContain("[overheard]", text);
    }

    [Fact]
    public void A_group_conversation_gives_the_handle_and_the_group_rules()
    {
        var text = AgentIdentity.Describe("CloudAgentBeta", "@CloudAgentBeta", "discord", ConversationKind.Group, "ignored");

        Assert.Contains("group conversation on discord", text);
        Assert.Contains("mention you there as @CloudAgentBeta", text);
        Assert.Contains("[overheard]", text);
        Assert.DoesNotContain("ignored", text);
    }

    [Fact]
    public void A_surface_that_does_not_know_its_handle_leaves_it_out()
    {
        var text = AgentIdentity.Describe("CloudAgent", null, "telegram", ConversationKind.Group, null);

        Assert.DoesNotContain("mention you there", text);
    }
}
