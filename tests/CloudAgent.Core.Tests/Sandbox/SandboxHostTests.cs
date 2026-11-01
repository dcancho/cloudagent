using CloudAgent.Core.Sandbox;
using CloudAgent.Core.Tools;
using CloudAgent.Core.Tools.Code;
using CloudAgent.Core.Workspaces;

namespace CloudAgent.Core.Tests.Sandbox;

public sealed class SandboxHostTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cloudagent-tests", Guid.NewGuid().ToString("N"));

    public SandboxHostTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeRunner : ISandboxProcessRunner
    {
        private readonly object _gate = new();

        public List<string[]> Calls { get; } = [];

        public List<TimeSpan> Timeouts { get; } = [];

        public Func<string[], Task<ProcessOutcome>> Handler { get; set; } = _ => Task.FromResult(new ProcessOutcome(0, "", "", false));

        public Task<ProcessOutcome> RunAsync(
            string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            string[] call = [executable, .. arguments];
            lock (_gate)
            {
                Calls.Add(call);
                Timeouts.Add(timeout);
            }
            return Handler(call);
        }

        public static string ModeOf(string[] call) => call[5];

        public List<string> Modes() => Calls.Select(ModeOf).ToList();

        public string[] Last(string mode) => Calls.Last(c => ModeOf(c) == mode);
    }

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }

    private static readonly SandboxOptions Options =
        new("/usr/local/bin/cloudagent-sandbox-run", "img:1", TimeSpan.FromSeconds(1200));

    private static SandboxExec Exec(string program = "dotnet", params string[] arguments) =>
        new(SandboxUsers.Runner, "", program, arguments, TimeoutSeconds: 120);

    private static (SandboxHost Host, FakeRunner Runner, FakeTime Time) Create(SandboxOptions? options = null)
    {
        var runner = new FakeRunner();
        var time = new FakeTime();
        return (new SandboxHost(runner, options ?? Options, time), runner, time);
    }

    [Fact]
    public async Task Nothing_starts_until_the_first_command()
    {
        var (host, runner, _) = Create();

        var session = host.SessionFor("web:1", "cloudagent", _directory);

        Assert.Empty(runner.Calls);
        Assert.False(session.IsRunning);

        await session.ExecAsync(Exec());

        Assert.Equal(["start", "exec"], runner.Modes());
        Assert.True(session.IsRunning);
    }

    [Fact]
    public async Task Later_commands_reuse_the_same_container()
    {
        var (host, runner, _) = Create();
        var session = host.SessionFor("web:1", "cloudagent", _directory);

        await session.ExecAsync(Exec());
        await session.ExecAsync(Exec("git", "status"));

        Assert.Equal(["start", "exec", "exec"], runner.Modes());
        Assert.Equal(runner.Calls[1][7], runner.Calls[2][7]);
    }

    [Fact]
    public async Task The_wrapper_is_called_through_sudo_as_the_sandbox_user_with_validated_parts()
    {
        var (host, runner, _) = Create();
        var session = host.SessionFor("web:1", "cloudagent", _directory);

        await session.ExecAsync(Exec("dotnet", "test") with { WorkingDirectory = "src", TimeoutSeconds = 300 });

        var start = runner.Last("start");
        Assert.Equal(["sudo", "-n", "-u", "cloudagent-sandbox", "/usr/local/bin/cloudagent-sandbox-run", "start"], start.Take(6));
        Assert.Contains("cloudagent", start);
        Assert.Contains("img:1", start);
        var exec = runner.Last("exec");
        Assert.Contains("runner", exec);
        Assert.Contains("/work/src", exec);
        Assert.Equal(["--timeout", "300"], exec.SkipWhile(a => a != "--timeout").Take(2));
        Assert.Equal(["--", "dotnet", "test"], exec.TakeLast(3));
    }

    [Fact]
    public async Task Exit_code_and_output_are_passed_through()
    {
        var (host, runner, _) = Create();
        runner.Handler = call => Task.FromResult(FakeRunner.ModeOf(call) == "exec" ? new ProcessOutcome(3, "out", "err", false) : new ProcessOutcome(0, "", "", false));

        var result = await host.SessionFor("web:1", "cloudagent", _directory).ExecAsync(Exec());

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out", result.Output);
        Assert.Equal("err", result.Error);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task A_session_idle_for_thirty_minutes_is_removed_and_the_next_command_starts_a_new_one()
    {
        var (host, runner, time) = Create();
        var session = host.SessionFor("web:1", "cloudagent", _directory);
        await session.ExecAsync(Exec());

        time.Advance(TimeSpan.FromMinutes(29));
        await host.ExpireAsync();
        Assert.True(session.IsRunning);

        time.Advance(TimeSpan.FromMinutes(2));
        await host.ExpireAsync();
        Assert.False(session.IsRunning);
        Assert.Equal(["start", "exec", "stop"], runner.Modes());

        await session.ExecAsync(Exec());
        Assert.Equal(["start", "exec", "stop", "start", "exec"], runner.Modes());
    }

    [Fact]
    public async Task A_session_is_removed_after_four_hours_even_if_it_is_in_use()
    {
        var (host, runner, time) = Create();
        var session = host.SessionFor("web:1", "cloudagent", _directory);
        await session.ExecAsync(Exec());

        for (var i = 0; i < 11; i++)
        {
            time.Advance(TimeSpan.FromMinutes(20));
            await session.ExecAsync(Exec());
        }
        Assert.Equal(1, runner.Modes().Count(m => m == "start"));

        time.Advance(TimeSpan.FromMinutes(20));
        await host.ExpireAsync();

        Assert.False(session.IsRunning);
        Assert.Equal("stop", runner.Modes().Last());
    }

    [Fact]
    public async Task A_command_after_the_life_limit_gets_a_fresh_container()
    {
        var (host, runner, time) = Create(Options with { IdleTimeout = TimeSpan.FromHours(10) });
        var session = host.SessionFor("web:1", "cloudagent", _directory);
        await session.ExecAsync(Exec());

        time.Advance(TimeSpan.FromHours(4));
        await session.ExecAsync(Exec());

        Assert.Equal(["start", "exec", "stop", "start", "exec"], runner.Modes());
    }

    [Fact]
    public async Task A_second_conversation_is_refused_while_the_first_holds_the_sandbox()
    {
        var (host, runner, _) = Create();
        var first = host.SessionFor("web:1", "cloudagent", _directory);
        var second = host.SessionFor("web:2", "cloudagent", _directory);
        await first.ExecAsync(Exec());

        await Assert.ThrowsAsync<SandboxBusyException>(() => second.ExecAsync(Exec()));

        Assert.Equal(["start", "exec"], runner.Modes());
        await host.ReleaseAsync("web:1");
        await second.ExecAsync(Exec());
        Assert.Equal(["start", "exec", "stop", "start", "exec"], runner.Modes());
    }

    [Fact]
    public async Task More_sessions_may_run_when_the_limit_allows()
    {
        var (host, _, _) = Create(Options with { MaxSessions = 2 });

        await host.SessionFor("web:1", "cloudagent", _directory).ExecAsync(Exec());
        await host.SessionFor("web:2", "cloudagent", _directory).ExecAsync(Exec());
        await Assert.ThrowsAsync<SandboxBusyException>(() => host.SessionFor("web:3", "cloudagent", _directory).ExecAsync(Exec()));
    }

    [Fact]
    public async Task A_session_that_failed_to_start_does_not_keep_the_slot()
    {
        var (host, runner, _) = Create();
        runner.Handler = call => Task.FromResult(FakeRunner.ModeOf(call) == "start" ? new ProcessOutcome(2, "", "image not allowed", false) : new ProcessOutcome(0, "", "", false));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.SessionFor("web:1", "cloudagent", _directory).ExecAsync(Exec()));
        Assert.Contains("image not allowed", error.Message);

        runner.Handler = _ => Task.FromResult(new ProcessOutcome(0, "", "", false));
        await host.SessionFor("web:2", "cloudagent", _directory).ExecAsync(Exec());
    }

    [Fact]
    public async Task Commands_in_one_session_run_one_at_a_time()
    {
        var (host, runner, _) = Create();
        var release = new TaskCompletionSource();
        var running = 0;
        var peak = 0;
        runner.Handler = async call =>
        {
            if (FakeRunner.ModeOf(call) == "exec")
            {
                var now = Interlocked.Increment(ref running);
                peak = Math.Max(peak, now);
                await release.Task;
                Interlocked.Decrement(ref running);
            }
            return new ProcessOutcome(0, "", "", false);
        };
        var session = host.SessionFor("web:1", "cloudagent", _directory);

        var first = session.ExecAsync(Exec());
        var second = session.ExecAsync(Exec());
        await Task.Delay(100);
        Assert.Equal(1, runner.Modes().Count(m => m == "exec"));
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, peak);
        Assert.Equal(2, runner.Modes().Count(m => m == "exec"));
    }

    [Fact]
    public async Task Disposing_the_host_stops_every_running_session()
    {
        var (host, runner, _) = Create(Options with { MaxSessions = 2 });
        await host.SessionFor("web:1", "cloudagent", _directory).ExecAsync(Exec());
        await host.SessionFor("web:2", "cloudagent", _directory).ExecAsync(Exec());

        await host.DisposeAsync();

        Assert.Equal(2, runner.Modes().Count(m => m == "stop"));
    }

    [Fact]
    public async Task Closing_a_conversation_stops_its_session_and_keeps_the_directory()
    {
        var (host, runner, _) = Create();
        var marker = Path.Combine(_directory, "work.txt");
        File.WriteAllText(marker, "kept");
        await host.SessionFor("web:1", "cloudagent", _directory).ExecAsync(Exec());

        await host.ReleaseAsync("web:1");

        Assert.Equal("stop", runner.Modes().Last());
        Assert.Equal("kept", File.ReadAllText(marker));
    }

    [Fact]
    public async Task Leftover_containers_are_reaped_once_when_asked()
    {
        var (host, runner, _) = Create();

        await host.ReapAsync();

        Assert.Equal(["reap"], runner.Modes());
    }

    [Fact]
    public async Task A_command_that_hits_its_limit_inside_the_container_leaves_the_session_running()
    {
        var (host, runner, _) = Create();
        runner.Handler = call => Task.FromResult(FakeRunner.ModeOf(call) == "exec" ? new ProcessOutcome(124, "so far", "", false) : new ProcessOutcome(0, "", "", false));
        var session = host.SessionFor("web:1", "cloudagent", _directory);

        var result = await session.ExecAsync(Exec());

        Assert.True(result.TimedOut);
        Assert.True(session.IsRunning);
        Assert.DoesNotContain("stop", runner.Modes());
    }

    [Fact]
    public async Task A_command_the_host_gave_up_on_removes_the_session()
    {
        var (host, runner, _) = Create();
        runner.Handler = call => Task.FromResult(FakeRunner.ModeOf(call) == "exec" ? new ProcessOutcome(-1, "", "", true) : new ProcessOutcome(0, "", "", false));
        var session = host.SessionFor("web:1", "cloudagent", _directory);

        var result = await session.ExecAsync(Exec());

        Assert.True(result.TimedOut);
        Assert.False(session.IsRunning);
        Assert.Equal("stop", runner.Modes().Last());
    }

    [Fact]
    public async Task A_container_that_vanished_is_replaced_once()
    {
        var (host, runner, _) = Create();
        var execs = 0;
        runner.Handler = call =>
        {
            if (FakeRunner.ModeOf(call) == "exec" && Interlocked.Increment(ref execs) == 1)
                return Task.FromResult(new ProcessOutcome(1, "", "Error response from daemon: No such container: x", false));
            return Task.FromResult(new ProcessOutcome(0, "ok", "", false));
        };

        var result = await host.SessionFor("web:1", "cloudagent", _directory).ExecAsync(Exec());

        Assert.Equal("ok", result.Output);
        Assert.Equal(["start", "exec", "stop", "start", "exec"], runner.Modes());
    }

    [Fact]
    public async Task The_host_side_wait_is_longer_than_the_in_container_limit()
    {
        var (host, runner, _) = Create();

        await host.SessionFor("web:1", "cloudagent", _directory).ExecAsync(Exec() with { TimeoutSeconds = 100 });

        var index = runner.Calls.FindIndex(c => FakeRunner.ModeOf(c) == "exec");
        Assert.True(runner.Timeouts[index] > TimeSpan.FromSeconds(100));
    }

    [Fact]
    public async Task Run_command_goes_through_the_session_with_the_same_message_format()
    {
        var (host, runner, _) = Create();
        runner.Handler = call => Task.FromResult(FakeRunner.ModeOf(call) == "exec" ? new ProcessOutcome(0, "Passed!", "warning", false) : new ProcessOutcome(0, "", "", false));
        var workspace = new Workspace(_directory, ExecutionPolicy.Allow(["dotnet"]));
        Directory.CreateDirectory(Path.Combine(_directory, "src"));
        var tool = new RunCommandTool(workspace, sandbox: host.SessionFor("web:1", "cloudagent", workspace.Root));

        var result = await tool.InvokeAsync(Input(("command", "dotnet test"), ("directory", "src")));

        Assert.True(result.Success);
        Assert.StartsWith("Exit code 0 after ", result.Content);
        Assert.Contains("Passed!", result.Content);
        Assert.Contains("warning", result.Content);
        Assert.Contains("/work/src", runner.Last("exec"));
    }

    [Fact]
    public async Task Run_command_reports_a_timeout_as_a_failure()
    {
        var (host, runner, _) = Create();
        runner.Handler = call => Task.FromResult(FakeRunner.ModeOf(call) == "exec" ? new ProcessOutcome(124, "so far", "", false) : new ProcessOutcome(0, "", "", false));
        var workspace = new Workspace(_directory, ExecutionPolicy.Allow(["dotnet"]));
        var tool = new RunCommandTool(workspace, sandbox: host.SessionFor("web:1", "cloudagent", workspace.Root));

        var result = await tool.InvokeAsync(Input(("command", "dotnet test")));

        Assert.False(result.Success);
        Assert.Contains("so far", result.Error);
    }

    [Fact]
    public async Task Run_command_tells_a_second_conversation_the_sandbox_is_in_use()
    {
        var (host, _, _) = Create();
        var workspace = new Workspace(_directory, ExecutionPolicy.Allow(["dotnet"]));
        var first = new RunCommandTool(workspace, sandbox: host.SessionFor("web:1", "cloudagent", workspace.Root));
        var second = new RunCommandTool(workspace, sandbox: host.SessionFor("web:2", "cloudagent", workspace.Root));
        await first.InvokeAsync(Input(("command", "dotnet --version")));

        var result = await second.InvokeAsync(Input(("command", "dotnet --version")));

        Assert.False(result.Success);
        Assert.Contains("in use", result.Error);
    }

    [Fact]
    public async Task Run_command_still_refuses_a_program_the_workspace_does_not_allow()
    {
        var (host, runner, _) = Create();
        var workspace = new Workspace(_directory, ExecutionPolicy.Allow(["dotnet"]));
        var tool = new RunCommandTool(workspace, sandbox: host.SessionFor("web:1", "cloudagent", workspace.Root));

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
