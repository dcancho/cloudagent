using CloudAgent.Core.People;

namespace CloudAgent.Core.Tests.People;

public sealed class PeopleFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloudagent-tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly PeopleFile _file;

    public PeopleFileTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "people.json");
        _file = new PeopleFile(_path);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void A_missing_file_is_created_by_the_first_person()
    {
        _file.Add("jose");

        Assert.Equal(["jose"], PeopleRegistry.Load(_path).People.Select(p => p.Id));
    }

    [Fact]
    public void Linked_accounts_resolve_through_the_registry_the_host_loads()
    {
        _file.Add("jose");
        _file.Link("jose", "discord:485305305144754176");

        var holder = PeopleRegistry.Load(_path).Resolve("discord", "485305305144754176");

        Assert.Equal("jose", holder?.Id);
    }

    [Fact]
    public void An_account_held_by_someone_else_is_refused_and_names_the_holder()
    {
        _file.Add("diego");
        _file.Add("jose");
        _file.Link("diego", "discord:1");

        var error = Assert.Throws<InvalidOperationException>(() => _file.Link("jose", "discord:1"));

        Assert.Contains("'diego'", error.Message);
        Assert.Empty(_file.List().Single(p => p.Id == "jose").Accounts);
    }

    [Theory]
    [InlineData("discord")]
    [InlineData("discord:")]
    [InlineData(":123")]
    [InlineData("Discord:1")]
    [InlineData("discord:1 2")]
    public void A_malformed_account_is_refused(string account)
    {
        _file.Add("jose");

        Assert.Throws<ArgumentException>(() => _file.Link("jose", account));
    }

    [Fact]
    public void An_unknown_person_cannot_be_linked_and_the_file_is_left_alone()
    {
        _file.Add("jose");
        var before = File.ReadAllText(_path);

        Assert.Throws<InvalidOperationException>(() => _file.Link("nadie", "discord:1"));

        Assert.Equal(before, File.ReadAllText(_path));
    }

    [Fact]
    public void An_invalid_person_id_is_refused()
    {
        Assert.Throws<ArgumentException>(() => _file.Add("José Pérez"));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void A_person_cannot_be_added_twice()
    {
        _file.Add("jose");

        Assert.Throws<InvalidOperationException>(() => _file.Add("jose"));
    }

    [Fact]
    public void Unlinking_frees_the_account_for_someone_else()
    {
        _file.Add("diego");
        _file.Add("jose");
        _file.Link("diego", "discord:1");

        _file.Unlink("discord:1");
        _file.Link("jose", "discord:1");

        Assert.Equal("jose", PeopleRegistry.Load(_path).Resolve("discord", "1")?.Id);
    }

    [Fact]
    public void Removing_a_person_drops_their_accounts_from_the_allowlist()
    {
        _file.Add("jose");
        _file.Link("jose", "discord:1");

        _file.Remove("jose");

        Assert.Null(PeopleRegistry.Load(_path).Resolve("discord", "1"));
    }

    [Fact]
    public void Each_change_keeps_the_previous_file_as_a_backup()
    {
        _file.Add("jose");
        var before = File.ReadAllText(_path);

        _file.Link("jose", "discord:1");

        Assert.Equal(before, File.ReadAllText(_path + ".bak"));
    }

    [Fact]
    public void A_file_written_by_hand_in_the_documented_shape_is_read_and_kept()
    {
        File.WriteAllText(_path, """{ "diego": { "accounts": ["webchat:diego@example.com", "discord:7"] } }""");

        _file.Add("jose");

        var diego = PeopleRegistry.Load(_path).Find("diego");
        Assert.Equal(["webchat:diego@example.com", "discord:7"], diego?.Accounts);
    }
}
