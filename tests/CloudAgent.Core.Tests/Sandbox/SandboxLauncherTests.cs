using CloudAgent.Core.Sandbox;
using CloudAgent.Core.Tools;
using CloudAgent.Core.Tools.Code;
using CloudAgent.Core.Workspaces;

namespace CloudAgent.Core.Tests.Sandbox;

public sealed class SandboxLauncherTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloudagent-tests", Guid.NewGuid().ToString("N"));

    public SandboxLauncherTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeRunner(params ProcessOutcome[] outcomes) : ISandboxProcessRunner
    {
        private readonly Queue<ProcessOutcome> _outcomes = new(outcomes);

        public List<(string Executable, string[] Arguments, TimeSpan Timeout)> Calls { get; } = [];

        public Task<ProcessOutcome> RunAsync(
            string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add((executable, [.. arguments], timeout));
            return Task.FromResult(_outcomes.Count > 0 ? _outcomes.Dequeue() : new ProcessOutcome(0, "", "", false));
        }
    }

    private static readonly SandboxOptions Options =
        new("/usr/local/bin/cloudagent-sandbox-run", "img:1", TimeSpan.FromSeconds(1200), "2g", "1.5");

    private SandboxCommand Command(string taskId = "task-abc123") =>
        new(taskId, "cloudagent", _directory, "", "dotnet", ["test"]);

    [Fact]
    public async Task Exit_code_and_output_are_passed_through()
    {
        var runner = new FakeRunner(new ProcessOutcome(3, "out", "err", false));
        var launcher = new SandboxLauncher(runner, Options, "cloudagent");

        var result = await launcher.RunAsync(Command(), TimeSpan.FromSeconds(60));

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out", result.Output);
        Assert.Equal("err", result.Error);
        Assert.False(result.TimedOut);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task The_wrapper_is_run_as_the_sandbox_user_with_validated_parts()
    {
        var runner = new FakeRunner();
        var launcher = new SandboxLauncher(runner, Options, "cloudagent");

        await launcher.RunAsync(Command() with { WorkingDirectory = "src" }, TimeSpan.FromSeconds(60));

        var call = Assert.Single(runner.Calls);
        Assert.Equal("sudo", call.Executable);
        Assert.Equal(["-n", "-u", "cloudagent-sandbox", "/usr/local/bin/cloudagent-sandbox-run", "run"], call.Arguments.Take(5));
        Assert.Equal(["--", "dotnet", "test"], call.Arguments.TakeLast(3));
        Assert.Contains("/work/src", call.Arguments);
        Assert.Contains("img:1", call.Arguments);
    }

    [Fact]
    public async Task The_shorter_of_the_requested_and_configured_limits_applies()
    {
        var runner = new FakeRunner();
        var launcher = new SandboxLauncher(runner, Options with { Timeout = TimeSpan.FromSeconds(30) }, "cloudagent");

        await launcher.RunAsync(Command(), TimeSpan.FromSeconds(600));
        await launcher.RunAsync(Command(), TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(30), runner.Calls[0].Timeout);
        Assert.Equal(TimeSpan.FromSeconds(5), runner.Calls[1].Timeout);
    }

    [Fact]
    public async Task A_timeout_kills_the_container_by_name_and_keeps_the_directory()
    {
        var marker = Path.Combine(_directory, "work.txt");
        File.WriteAllText(marker, "kept");
        var runner = new FakeRunner(new ProcessOutcome(-1, "partial", "", true));
        var launcher = new SandboxLauncher(runner, Options, "cloudagent");

        var result = await launcher.RunAsync(Command("task-slow01"), TimeSpan.FromSeconds(1));

        Assert.True(result.TimedOut);
        Assert.Equal("partial", result.Output);
        Assert.Equal(2, runner.Calls.Count);
        var kill = runner.Calls[1];
        Assert.Equal(["kill", "--task", "task-slow01"], kill.Arguments.TakeLast(3));
        Assert.True(File.Exists(marker));
        Assert.Equal("kept", File.ReadAllText(marker));
        Assert.Equal(_directory, result.TaskDirectory);
    }

    [Fact]
    public async Task A_command_that_finishes_is_not_killed()
    {
        var runner = new FakeRunner(new ProcessOutcome(0, "ok", "", false));
        var launcher = new SandboxLauncher(runner, Options, "cloudagent");

        await launcher.RunAsync(Command(), TimeSpan.FromSeconds(60));

        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Contains("kill"));
    }

    [Fact]
    public async Task A_bad_task_id_never_reaches_the_runner()
    {
        var runner = new FakeRunner();
        var launcher = new SandboxLauncher(runner, Options, "cloudagent");

        await Assert.ThrowsAsync<ArgumentException>(() => launcher.RunAsync(Command("Bad;Id"), TimeSpan.FromSeconds(60)));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Run_command_goes_through_the_sandbox_when_one_is_given()
    {
        var runner = new FakeRunner(new ProcessOutcome(0, "Passed!", "warning", false));
        var workspace = new Workspace(_directory, ExecutionPolicy.Allow(["dotnet"]));
        var tool = new RunCommandTool(workspace, sandbox: new SandboxLauncher(runner, Options, "cloudagent"));
        Directory.CreateDirectory(Path.Combine(_directory, "src"));

        var result = await tool.InvokeAsync(Input(("command", "dotnet test"), ("directory", "src")));

        Assert.True(result.Success);
        Assert.Contains("Exit code 0", result.Content);
        Assert.Contains("Passed!", result.Content);
        Assert.Contains("warning", result.Content);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("sudo", call.Executable);
        Assert.Contains("/work/src", call.Arguments);
        Assert.Contains("dotnet", call.Arguments);
    }

    [Fact]
    public async Task Run_command_reports_a_timeout_as_a_failure()
    {
        var runner = new FakeRunner(new ProcessOutcome(-1, "so far", "", true));
        var workspace = new Workspace(_directory, ExecutionPolicy.Allow(["dotnet"]));
        var tool = new RunCommandTool(workspace, sandbox: new SandboxLauncher(runner, Options, "cloudagent"));

        var result = await tool.InvokeAsync(Input(("command", "dotnet test")));

        Assert.False(result.Success);
        Assert.Contains("so far", result.Error);
    }

    [Fact]
    public async Task Run_command_still_refuses_a_program_the_workspace_does_not_allow()
    {
        var runner = new FakeRunner();
        var workspace = new Workspace(_directory, ExecutionPolicy.Allow(["dotnet"]));
        var tool = new RunCommandTool(workspace, sandbox: new SandboxLauncher(runner, Options, "cloudagent"));

        var result = await tool.InvokeAsync(Input(("command", "rm -rf /")));

        Assert.False(result.Success);
        Assert.Empty(runner.Calls);
    }

    private static ToolInput Input(params (string Key, object Value)[] pairs)
    {
        var json = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in pairs)
            json[key] = System.Text.Json.Nodes.JsonValue.Create(value);
        return new ToolInput(json);
    }
}
