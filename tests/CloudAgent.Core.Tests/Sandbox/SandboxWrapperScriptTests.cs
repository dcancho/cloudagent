using System.Diagnostics;
using CloudAgent.Core.Sandbox;

namespace CloudAgent.Core.Tests.Sandbox;

public sealed class SandboxWrapperScriptTests : IDisposable
{
    private static readonly string? Bash = FindBash();
    private static readonly string? Script = FindScript();

    private static readonly SandboxOptions Options =
        new("x", "cloudagent-sandbox-dotnet:latest", TimeSpan.FromMinutes(20), "2g", "1.5", "1g");

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

    private static bool Unavailable => Bash is null || Script is null;

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

    private static string[] Replace(string[] args, string flag, string value)
    {
        var copy = args.ToArray();
        copy[Array.IndexOf(copy, flag) + 1] = value;
        return copy;
    }

    private static readonly string[] Start =
        ["start", "--session", "s-2f9c1a", "--repo", "cloudagent", "--image", "cloudagent-sandbox-dotnet:latest",
         "--memory", "2g", "--cpus", "1.5", "--tmp-size", "1g"];

    private static readonly string[] Exec =
        ["exec", "--session", "s-2f9c1a", "--as", "runner", "--workdir", "/work/src", "--timeout", "300",
         "--env", "GIT_PAGER=cat", "--", "dotnet", "test"];

    [Fact]
    public void Start_prints_the_command_the_builder_produces()
    {
        if (Unavailable)
            return;

        var (code, output, error) = Run(Start);

        Assert.True(code == 0, error);
        var expected = DockerRunBuilder.BuildStart(new SandboxStart("s-2f9c1a", "cloudagent", "/srv/tasks/cloudagent"), Options);
        Assert.Equal("docker " + string.Join(' ', expected), output);
    }

    [Fact]
    public void Exec_prints_the_command_the_builder_produces_wrapped_in_timeout()
    {
        if (Unavailable)
            return;

        var (code, output, error) = Run(Exec);

        Assert.True(code == 0, error);
        var expected = DockerRunBuilder.BuildExec("s-2f9c1a", new SandboxExec(
            SandboxUsers.Runner, "src", "dotnet", ["test"], new Dictionary<string, string> { ["GIT_PAGER"] = "cat" }, 300));
        Assert.Equal("docker " + string.Join(' ', expected), output);
        Assert.Contains(" timeout -k 10 300 dotnet test", output);
    }

    [Fact]
    public void Stop_and_reap_print_their_commands()
    {
        if (Unavailable)
            return;

        var (stopCode, stop, _) = Run("stop", "--session", "s-2f9c1a");
        var (reapCode, reap, _) = Run("reap");

        Assert.Equal(0, stopCode);
        Assert.Equal("docker " + string.Join(' ', DockerRunBuilder.BuildStop("s-2f9c1a")), stop);
        Assert.Equal(0, reapCode);
        Assert.StartsWith("docker ps -a --filter name=^cloudagent-sbx-", reap);
    }

    [Fact]
    public void Start_never_widens_the_container_and_mounts_once()
    {
        if (Unavailable)
            return;

        var (_, output, _) = Run(Start);

        Assert.DoesNotContain("--privileged", output);
        Assert.DoesNotContain("docker.sock", output);
        Assert.DoesNotContain("--network host", output);
        Assert.Contains("--entrypoint timeout", output);
        Assert.Equal(1, output.Split(" -v ").Length - 1);
    }

    [Theory]
    [InlineData("--session", "Bad;Id")]
    [InlineData("--session", "ab")]
    [InlineData("--session", "s-1 --privileged")]
    [InlineData("--repo", "unknown")]
    [InlineData("--repo", "../etc")]
    [InlineData("--image", "ubuntu:latest")]
    [InlineData("--image", "cloudagent-sandbox-dotnet:latest --privileged")]
    [InlineData("--memory", "999999999999")]
    [InlineData("--cpus", "all")]
    [InlineData("--tmp-size", "lots")]
    [InlineData("--tmp-size", "0g")]
    public void Start_refuses_input_outside_the_allowlists(string flag, string value)
    {
        if (Unavailable)
            return;

        var (code, output, error) = Run(Replace(Start, flag, value));

        Assert.NotEqual(0, code);
        Assert.Equal("", output);
        Assert.Contains("cloudagent-sandbox-run:", error);
    }

    [Theory]
    [InlineData("--as", "root")]
    [InlineData("--as", "coder")]
    [InlineData("--timeout", "0")]
    [InlineData("--timeout", "1201")]
    [InlineData("--timeout", "10;reboot")]
    [InlineData("--timeout", "-1")]
    [InlineData("--workdir", "/work/../etc")]
    [InlineData("--workdir", "/etc")]
    [InlineData("--session", "x; reboot")]
    public void Exec_refuses_bad_input(string flag, string value)
    {
        if (Unavailable)
            return;

        var (code, output, _) = Run(Replace(Exec, flag, value));

        Assert.NotEqual(0, code);
        Assert.Equal("", output);
    }

    [Theory]
    [InlineData("HTTP_PROXY=http://evil:1")]
    [InlineData("ANTHROPIC_API_KEY=secret")]
    [InlineData("GIT_PAGER=cat; id")]
    [InlineData("GIT_PAGER")]
    public void Exec_refuses_environment_outside_the_allowlist(string entry)
    {
        if (Unavailable)
            return;

        var (code, _, _) = Run(Replace(Exec, "--env", entry));

        Assert.NotEqual(0, code);
    }

    [Theory]
    [InlineData("/bin/sh")]
    [InlineData("-c")]
    [InlineData("a b")]
    [InlineData(".hidden")]
    public void Exec_refuses_a_program_name_that_is_not_plain(string program)
    {
        if (Unavailable)
            return;

        var args = Exec.ToArray();
        args[Array.IndexOf(args, "dotnet")] = program;
        var (code, _, _) = Run(args);

        Assert.NotEqual(0, code);
    }

    [Fact]
    public void Exec_without_a_command_and_unknown_modes_are_refused()
    {
        if (Unavailable)
            return;

        var (noCommand, _, _) = Run("exec", "--session", "s-2f9c1a", "--as", "runner", "--timeout", "10");
        var (unknown, _, _) = Run("shell", "--session", "s-2f9c1a");
        var (stray, _, _) = Run("stop", "--session", "s-2f9c1a", "--", "id");
        var (reapArgs, _, _) = Run("reap", "--session", "s-2f9c1a");

        Assert.NotEqual(0, noCommand);
        Assert.NotEqual(0, unknown);
        Assert.NotEqual(0, stray);
        Assert.NotEqual(0, reapArgs);
    }
}
