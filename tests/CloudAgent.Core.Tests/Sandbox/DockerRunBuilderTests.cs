using CloudAgent.Core.Sandbox;

namespace CloudAgent.Core.Tests.Sandbox;

public sealed class DockerRunBuilderTests
{
    private static readonly SandboxOptions Options =
        new("/usr/local/bin/cloudagent-sandbox-run", "cloudagent-sandbox-dotnet:latest", TimeSpan.FromMinutes(20), "2g", "1.5");

    private static SandboxCommand Command(
        string program = "dotnet", string[]? arguments = null, Dictionary<string, string>? environment = null, string workdir = "") =>
        new("task-2f9c1a", "cloudagent", "/srv/tasks/cloudagent", workdir, program, arguments ?? ["test"], environment);

    private static string Value(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Fact]
    public void Every_isolation_and_limit_flag_is_present()
    {
        var args = DockerRunBuilder.Build(Command(), Options);

        Assert.Equal("run", args[0]);
        Assert.Contains("--rm", args);
        Assert.Equal("cloudagent-sbx-task-2f9c1a", Value(args, "--name"));
        Assert.Equal("cloudagent-sandbox-internal", Value(args, "--network"));
        Assert.Contains("--cap-drop=ALL", args);
        Assert.Equal("no-new-privileges", Value(args, "--security-opt"));
        Assert.Equal("512", Value(args, "--pids-limit"));
        Assert.Equal("2g", Value(args, "--memory"));
        Assert.Equal("1.5", Value(args, "--cpus"));
        Assert.Contains("--read-only", args);
        Assert.StartsWith("/tmp:", Value(args, "--tmpfs"));
        Assert.Equal("1000:1000", Value(args, "--user"));
        Assert.Equal("never", Value(args, "--pull"));
    }

    [Fact]
    public void The_task_directory_is_the_only_mount()
    {
        var args = DockerRunBuilder.Build(Command(workdir: "src/App"), Options);

        Assert.Equal(["-v"], args.Where(a => a is "-v" or "--volume" or "--mount"));
        Assert.Equal("/srv/tasks/cloudagent:/work:rw", Value(args, "-v"));
        Assert.Equal("/work/src/App", Value(args, "-w"));
    }

    [Fact]
    public void Nothing_that_widens_the_container_is_ever_added()
    {
        var hostile = new Dictionary<string, string> { ["DOCKER_HOST"] = "tcp://x", ["TERM"] = "xterm" };
        var args = DockerRunBuilder.Build(Command("git", ["status"], hostile), Options);
        var text = string.Join(' ', args);

        Assert.DoesNotContain("--privileged", args);
        Assert.DoesNotContain("docker.sock", text);
        Assert.NotEqual("host", Value(args, "--network"));
        Assert.DoesNotContain(args, a => a.StartsWith("--pid=", StringComparison.Ordinal) || a == "--pid" || a.StartsWith("--ipc", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("--cap-add", StringComparison.Ordinal) || a.StartsWith("--device", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_allowlisted_variables_reach_the_container_and_the_proxy_is_always_set()
    {
        var environment = new Dictionary<string, string>
        {
            ["GIT_PAGER"] = "cat",
            ["ANTHROPIC_API_KEY"] = "secret",
            ["HTTP_PROXY"] = "http://elsewhere:1",
            ["DISCORD_BOT_TOKEN"] = "secret",
        };
        var args = DockerRunBuilder.Build(Command(environment: environment), Options);
        var variables = args.Where((a, i) => i > 0 && args[i - 1] == "-e").ToList();

        Assert.Contains("GIT_PAGER=cat", variables);
        Assert.DoesNotContain(variables, v => v.Contains("secret", StringComparison.Ordinal));
        Assert.DoesNotContain(variables, v => v.StartsWith("DISCORD", StringComparison.Ordinal));
        Assert.Contains("HTTP_PROXY=http://cloudagent-egress:3128", variables);
        Assert.Contains("HTTPS_PROXY=http://cloudagent-egress:3128", variables);
        Assert.Contains("NO_PROXY=localhost,127.0.0.1", variables);
        Assert.Single(variables, v => v.StartsWith("HTTP_PROXY=", StringComparison.Ordinal));
    }

    [Fact]
    public void The_image_and_the_command_come_last()
    {
        var args = DockerRunBuilder.Build(Command("dotnet", ["build", "-c", "Release"]), Options);

        Assert.Equal(["cloudagent-sandbox-dotnet:latest", "dotnet", "build", "-c", "Release"], args.TakeLast(5));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("Task-1")]
    [InlineData("-task-1")]
    [InlineData("task 1")]
    [InlineData("task;rm")]
    [InlineData("task/../x")]
    public void A_task_id_that_is_not_plain_is_refused(string taskId)
    {
        var command = Command() with { TaskId = taskId };

        Assert.Throws<ArgumentException>(() => DockerRunBuilder.Build(command, Options));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("a/../../outside")]
    [InlineData("/etc")]
    public void A_working_directory_outside_the_task_directory_is_refused(string workdir)
    {
        Assert.Throws<ArgumentException>(() => DockerRunBuilder.Build(Command(workdir: workdir), Options));
    }

    [Fact]
    public void A_windows_style_relative_directory_is_normalized()
    {
        Assert.Equal("/work/src/App", DockerRunBuilder.ContainerWorkingDirectory(@"src\App"));
        Assert.Equal("/work", DockerRunBuilder.ContainerWorkingDirectory("."));
    }
}
