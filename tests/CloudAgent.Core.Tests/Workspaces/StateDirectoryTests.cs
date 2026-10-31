using CloudAgent.Core.Tools;
using CloudAgent.Core.Tools.Files;
using CloudAgent.Core.Workspaces;
using Xunit;

namespace CloudAgent.Core.Tests.Workspaces;

/// <summary>The host's state folder holds every conversation's records, so no file tool may reach it.</summary>
public sealed class StateDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudagent-tests", Guid.NewGuid().ToString("N"));
    private readonly Workspace _workspace;

    public StateDirectoryTests()
    {
        _workspace = new Workspace(_root);
        Directory.CreateDirectory(Path.Combine(_root, Workspace.StateDirectory, "memory", "pending"));
        File.WriteAllText(Path.Combine(_root, Workspace.StateDirectory, "memory", "pending", "p1.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "hello");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(".agent")]
    [InlineData(".agent/memory/pending/p1.json")]
    [InlineData("./.agent/../.agent/memory")]
    [InlineData("notes/../.agent")]
    public void Paths_into_the_state_folder_are_refused(string path)
    {
        Assert.Throws<WorkspaceAccessException>(() => _workspace.Resolve(path));
    }

    [Fact]
    public async Task Listing_the_root_hides_the_state_folder()
    {
        var result = await new ListDirectoryTool(_workspace).InvokeAsync(ToolInput.FromJson("{}"));

        Assert.Contains("notes.txt", result.Content);
        Assert.DoesNotContain(".agent", result.Content);
    }

    [Fact]
    public void Ordinary_paths_still_resolve()
    {
        Assert.Equal(Path.Combine(_workspace.Root, "notes.txt"), _workspace.Resolve("notes.txt"));
    }
}
