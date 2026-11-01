using CloudAgent.Core.Sandbox;

namespace CloudAgent.Core.Tests.Sandbox;

public sealed class DockerRunBuilderTests
{
    private static readonly SandboxOptions Options =
        new("/usr/local/bin/cloudagent-sandbox-run", "cloudagent-sandbox-dotnet:latest", TimeSpan.FromMinutes(20), "2g", "1.5", "1g");

    private static readonly SandboxStart Start = new("s-2f9c1a", "cloudagent", "/srv/tasks/cloudagent");

    private static SandboxExec Exec(
        string program = "dotnet", string[]? arguments = null, Dictionary<string, string>? environment = null,
        string workdir = "", int seconds = 120) =>
        new(SandboxUsers.Runner, workdir, program, arguments ?? ["test"], environment, seconds);

    private static string Value(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Fact]
    public void Every_isolation_and_limit_flag_is_present_on_start()
    {
        var args = DockerRunBuilder.BuildStart(Start, Options);

        Assert.Equal("run", args[0]);
        Assert.Contains("-d", args);
        Assert.Contains("--rm", args);
        Assert.Contains("--init", args);
        Assert.Equal("never", Value(args, "--pull"));
        Assert.Equal("cloudagent-sbx-s-2f9c1a", Value(args, "--name"));
        Assert.Equal("cloudagent-sandbox-internal", Value(args, "--network"));
        Assert.Contains("--cap-drop=ALL", args);
        Assert.Equal("no-new-privileges", Value(args, "--security-opt"));
        Assert.Equal("512", Value(args, "--pids-limit"));
        Assert.Equal("2g", Value(args, "--memory"));
        Assert.Equal("1.5", Value(args, "--cpus"));
        Assert.Contains("--read-only", args);
        Assert.Equal("/tmp:rw,exec,nosuid,size=1g", Value(args, "--tmpfs"));
        Assert.Equal("1000:1000", Value(args, "--user"));
    }

    [Fact]
    public void The_container_ends_by_itself_after_the_maximum_life()
    {
        var args = DockerRunBuilder.BuildStart(Start, Options);

        Assert.Equal("timeout", Value(args, "--entrypoint"));
        Assert.Equal(["cloudagent-sandbox-dotnet:latest", "14400", "sleep", "infinity"], args.TakeLast(4));
    }

    [Fact]
    public void The_task_directory_is_the_only_mount()
    {
        var args = DockerRunBuilder.BuildStart(Start, Options);

        Assert.Equal(["-v"], args.Where(a => a is "-v" or "--volume" or "--mount"));
        Assert.Equal("/srv/tasks/cloudagent:/work:rw", Value(args, "-v"));
        Assert.Equal("/work", Value(args, "-w"));
    }

    [Fact]
    public void Nothing_that_widens_the_container_is_ever_added()
    {
        var args = DockerRunBuilder.BuildStart(Start, Options).Concat(DockerRunBuilder.BuildExec("s-2f9c1a", Exec())).ToList();
        var text = string.Join(' ', args);

        Assert.DoesNotContain("--privileged", args);
        Assert.DoesNotContain("docker.sock", text);
        Assert.NotEqual("host", Value(args, "--network"));
        Assert.DoesNotContain(args, a => a == "--pid" || a.StartsWith("--pid=", StringComparison.Ordinal) || a.StartsWith("--ipc", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("--cap-add", StringComparison.Ordinal) || a.StartsWith("--device", StringComparison.Ordinal));
    }

    [Fact]
    public void The_proxy_is_always_set_at_start()
    {
        var args = DockerRunBuilder.BuildStart(Start, Options);
        var variables = args.Where((a, i) => i > 0 && args[i - 1] == "-e").ToList();

        Assert.Contains("HTTP_PROXY=http://cloudagent-egress:3128", variables);
        Assert.Contains("HTTPS_PROXY=http://cloudagent-egress:3128", variables);
        Assert.Contains("NO_PROXY=localhost,127.0.0.1", variables);
    }

    [Fact]
    public void Exec_runs_as_the_runner_and_wraps_the_program_with_timeout()
    {
        var args = DockerRunBuilder.BuildExec("s-2f9c1a", Exec("dotnet", ["build", "-c", "Release"], workdir: "src/App", seconds: 300));

        Assert.Equal("exec", args[0]);
        Assert.Equal("1000:1000", Value(args, "-u"));
        Assert.Equal("/work/src/App", Value(args, "-w"));
        Assert.Equal(
            ["cloudagent-sbx-s-2f9c1a", "timeout", "-k", "10", "300", "dotnet", "build", "-c", "Release"],
            args.TakeLast(9));
    }

    [Fact]
    public void Only_allowlisted_variables_reach_an_exec()
    {
        var environment = new Dictionary<string, string>
        {
            ["GIT_PAGER"] = "cat",
            ["ANTHROPIC_API_KEY"] = "secret",
            ["HTTP_PROXY"] = "http://elsewhere:1",
            ["DISCORD_BOT_TOKEN"] = "secret",
        };
        var args = DockerRunBuilder.BuildExec("s-2f9c1a", Exec(environment: environment));
        var variables = args.Where((a, i) => i > 0 && args[i - 1] == "-e").ToList();

        Assert.Equal(["GIT_PAGER=cat"], variables);
    }

    [Fact]
    public void Stop_removes_the_container_by_name()
    {
        Assert.Equal(["rm", "-f", "cloudagent-sbx-s-2f9c1a"], DockerRunBuilder.BuildStop("s-2f9c1a"));
    }

    [Fact]
    public void An_unknown_user_is_refused()
    {
        Assert.Throws<ArgumentException>(() => DockerRunBuilder.BuildExec("s-2f9c1a", Exec() with { User = "root" }));
        Assert.Throws<ArgumentException>(() => SandboxUsers.ContainerUser("coder"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("Task-1")]
    [InlineData("-task-1")]
    [InlineData("task 1")]
    [InlineData("task;rm")]
    [InlineData("task/../x")]
    public void A_session_id_that_is_not_plain_is_refused(string id)
    {
        Assert.Throws<ArgumentException>(() => DockerRunBuilder.BuildStart(Start with { SessionId = id }, Options));
        Assert.Throws<ArgumentException>(() => DockerRunBuilder.BuildExec(id, Exec()));
        Assert.Throws<ArgumentException>(() => DockerRunBuilder.BuildStop(id));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("a/../../outside")]
    [InlineData("/etc")]
    public void A_working_directory_outside_the_task_directory_is_refused(string workdir)
    {
        Assert.Throws<ArgumentException>(() => DockerRunBuilder.BuildExec("s-2f9c1a", Exec(workdir: workdir)));
    }

    [Fact]
    public void A_windows_style_relative_directory_is_normalized()
    {
        Assert.Equal("/work/src/App", DockerRunBuilder.ContainerWorkingDirectory(@"src\App"));
        Assert.Equal("/work", DockerRunBuilder.ContainerWorkingDirectory("."));
    }
}
