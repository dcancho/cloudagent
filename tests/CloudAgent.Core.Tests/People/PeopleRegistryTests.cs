using CloudAgent.Core.People;
using Xunit;

namespace CloudAgent.Core.Tests.People;

public sealed class PeopleRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudagent-tests", Guid.NewGuid().ToString("N"));

    public PeopleRegistryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PeopleRegistry Load(string json)
    {
        var path = Path.Combine(_root, "people.json");
        File.WriteAllText(path, json);
        return PeopleRegistry.Load(path);
    }

    [Fact]
    public void Without_a_file_only_local_accounts_get_in()
    {
        var people = PeopleRegistry.Load(Path.Combine(_root, "missing.json"));

        Assert.True(people.Resolve("cli", "local")?.Owner);
        Assert.True(people.Resolve("webchat", "local")?.Owner);
        Assert.Null(people.Resolve("telegram", "123"));
        Assert.Null(people.Resolve("webchat", "someone@example.com"));
    }

    [Fact]
    public void Accounts_on_different_surfaces_resolve_to_the_same_person()
    {
        var people = Load("""
            {
              "diego": { "owner": true, "accounts": ["telegram:123", "discord:456"] },
              "ana": { "accounts": ["discord:789"] }
            }
            """);

        Assert.Equal("diego", people.Resolve("telegram", "123")?.Id);
        Assert.Equal("diego", people.Resolve("discord", "456")?.Id);
        Assert.Equal("ana", people.Resolve("discord", "789")?.Id);
        Assert.False(people.Resolve("discord", "789")!.Owner);
        Assert.Null(people.Resolve("cli", "local"));
    }

    [Fact]
    public void An_account_linked_to_two_people_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Load("""
            { "diego": { "accounts": ["discord:1"] }, "ana": { "accounts": ["discord:1"] } }
            """));
    }

    [Fact]
    public void Two_owners_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Load("""
            { "diego": { "owner": true, "accounts": [] }, "ana": { "owner": true, "accounts": [] } }
            """));
    }
}
