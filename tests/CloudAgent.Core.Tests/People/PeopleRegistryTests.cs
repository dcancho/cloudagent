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
    public void Without_a_file_nobody_is_known()
    {
        var people = PeopleRegistry.Load(Path.Combine(_root, "missing.json"));

        Assert.Empty(people.People);
        Assert.Null(people.Resolve("cli", PeopleRegistry.AnonymousSender));
        Assert.Null(people.Resolve("telegram", "123"));
    }

    [Fact]
    public void Accounts_on_different_surfaces_resolve_to_the_same_person()
    {
        var people = Load("""
            {
              "diego": { "accounts": ["telegram:123", "discord:456"] },
              "jose": { "accounts": ["discord:789"] }
            }
            """);

        Assert.Equal("diego", people.Resolve("telegram", "123")?.Id);
        Assert.Equal("diego", people.Resolve("discord", "456")?.Id);
        Assert.Equal("jose", people.Resolve("discord", "789")?.Id);
        Assert.Equal("jose", people.Find("jose")?.Id);
        Assert.Null(people.Find("ana"));
    }

    [Fact]
    public void An_account_linked_to_two_people_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Load("""
            { "diego": { "accounts": ["discord:1"] }, "jose": { "accounts": ["discord:1"] } }
            """));
    }

    [Theory]
    [InlineData(PeopleRegistry.AnonymousSender)]
    [InlineData("Ana")]
    [InlineData("ana:work")]
    [InlineData("ana.work")]
    [InlineData("-ana")]
    [InlineData("")]
    public void Ids_that_could_collide_as_file_names_are_rejected(string id)
    {
        Assert.False(PeopleRegistry.IsValidId(id));
        Assert.Throws<ArgumentException>(() => new PeopleRegistry([new Person(id, [])]));
    }

    [Fact]
    public void The_unidentified_sender_cannot_be_linked_to_a_person()
    {
        Assert.Throws<ArgumentException>(() => Load($$"""
            { "diego": { "accounts": ["webchat:{{PeopleRegistry.AnonymousSender}}"] } }
            """));
        Assert.Null(PeopleRegistry.Empty.Resolve("webchat", PeopleRegistry.AnonymousSender));
    }

    [Theory]
    [InlineData("diego")]
    [InlineData("ana_work")]
    [InlineData("jose-2")]
    public void Plain_ids_are_accepted(string id) => Assert.True(PeopleRegistry.IsValidId(id));
}
