using System.Diagnostics;
using CloudAgent.Core.Sandbox;

namespace CloudAgent.Core.Tests.Sandbox;

public sealed class SandboxWrapperScriptTests : IDisposable
{
    private static readonly string? Bash = FindBash();
    private static readonly string? Script = FindScript();

    private readonly string _config = Path.Combine(Path.GetTempPath(), "cloudagent-tests", Guid.NewGuid().ToString("N"));

    public SandboxWrapperScriptTests()
    {
        Directory.CreateDirectory(_config);
        File.WriteAllText(Path.Combine(_config, "sandbox-repos"), "# repositories\ncloudagent=/srv/tasks/cloudagent\n");
        File.WriteAllText(Path.Combine(_config, "sandbox-images"), "# images\ncloudagent-sandbox-dotnet:latest\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_config))
            Directory.Delete(_config, recursive: true);
    }

    private static string? FindBash()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe"]
            : ["/usr/bin/bash", "/bin/bash"];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "deploy", "sandbox", "cloudagent-sandbox-run");
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    private static string Posix(string path) => path.Replace('\\', '/');

    private (int ExitCode, string Output, string Error) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Bash!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Posix(Script!));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["DRY_RUN"] = "1";
        start.Environment["SANDBOX_CONFIG_DIR"] = Posix(_config);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result.Trim(), error.Result.Trim());
    }

    private static string[] Valid(params string[] overrides)
    {
        var args = new Dictionary<string, string>
        {
            ["--task"] = "task-2f9c1a",
            ["--repo"] = "cloudagent",
            ["--image"] = "cloudagent-sandbox-dotnet:latest",
            ["--memory"] = "2g",
            ["--cpus"] = "1.5",
            ["--workdir"] = "/work/src",
        };
        for (var i = 0; i + 1 < overrides.Length; i += 2)
            args[overrides[i]] = overrides[i + 1];
        return ["run", .. args.SelectMany(pair => new[] { pair.Key, pair.Value }), "--env", "GIT_PAGER=cat", "--", "dotnet", "test"];
    }

    [Fact]
    public void Valid_input_prints_the_command_the_builder_produces()
    {
        if (Bash is null || Script is null)
            return;

        var (code, output, error) = Run(Valid());

        Assert.True(code == 0, error);
        var options = new SandboxOptions("x", "cloudagent-sandbox-dotnet:latest", TimeSpan.FromMinutes(20));
        var command = new SandboxCommand(
            "task-2f9c1a", "cloudagent", "/srv/tasks/cloudagent", "src", "dotnet", ["test"],
            new Dictionary<string, string> { ["GIT_PAGER"] = "cat" });
        Assert.Equal("docker " + string.Join(' ', DockerRunBuilder.Build(command, options)), output);
    }

    [Fact]
    public void The_command_never_widens_the_container()
    {
        if (Bash is null || Script is null)
            return;

        var (_, output, _) = Run(Valid());

        Assert.DoesNotContain("--privileged", output);
        Assert.DoesNotContain("docker.sock", output);
        Assert.DoesNotContain("--network host", output);
        Assert.Equal(1, output.Split(" -v ").Length - 1);
    }

    [Theory]
    [InlineData("--task", "Bad;Id")]
    [InlineData("--task", "ab")]
    [InlineData("--task", "task-1 --privileged")]
    [InlineData("--repo", "unknown")]
    [InlineData("--repo", "../etc")]
    [InlineData("--image", "ubuntu:latest")]
    [InlineData("--image", "cloudagent-sandbox-dotnet:latest --privileged")]
    [InlineData("--memory", "999999999999")]
    [InlineData("--cpus", "all")]
    [InlineData("--workdir", "/work/../etc")]
    [InlineData("--workdir", "/etc")]
    public void Input_outside_the_allowlists_is_refused(string flag, string value)
    {
        if (Bash is null || Script is null)
            return;

        var (code, output, error) = Run(Valid(flag, value));

        Assert.NotEqual(0, code);
        Assert.Equal("", output);
        Assert.Contains("cloudagent-sandbox-run:", error);
    }

    [Fact]
    public void An_environment_variable_outside_the_allowlist_is_refused()
    {
        if (Bash is null || Script is null)
            return;

        var (code, _, _) = Run("run", "--task", "task-2f9c1a", "--repo", "cloudagent", "--image", "cloudagent-sandbox-dotnet:latest",
            "--env", "HTTP_PROXY=http://evil:1", "--", "dotnet", "test");

        Assert.NotEqual(0, code);
    }

    [Fact]
    public void A_program_name_that_is_not_plain_is_refused()
    {
        if (Bash is null || Script is null)
            return;

        var (code, _, _) = Run("run", "--task", "task-2f9c1a", "--repo", "cloudagent", "--image", "cloudagent-sandbox-dotnet:latest",
            "--", "/bin/sh", "-c", "id");

        Assert.NotEqual(0, code);
    }

    [Fact]
    public void Kill_names_the_container_and_refuses_a_bad_task_id()
    {
        if (Bash is null || Script is null)
            return;

        var (code, output, _) = Run("kill", "--task", "task-2f9c1a");
        var (badCode, _, _) = Run("kill", "--task", "x; reboot");

        Assert.Equal(0, code);
        Assert.Equal("docker kill cloudagent-sbx-task-2f9c1a", output);
        Assert.NotEqual(0, badCode);
    }
}
