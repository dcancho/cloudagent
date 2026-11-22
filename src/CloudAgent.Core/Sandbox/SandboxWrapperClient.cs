using System.Globalization;

namespace CloudAgent.Core.Sandbox;

/// <summary>Drives the wrapper script, through sudo as the sandbox user, one mode per method.</summary>
public sealed class SandboxWrapperClient(ISandboxProcessRunner runner, SandboxOptions options)
{
    private static readonly TimeSpan Housekeeping = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ExecGrace = TimeSpan.FromSeconds(40);

    public Task<ProcessOutcome> StartAsync(SandboxStart start, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            ["start", "--session", start.SessionId, "--repo", start.Repository, "--image", options.Image,
             "--memory", options.Memory, "--cpus", options.Cpus, "--tmp-size", options.TmpSize],
            Housekeeping, cancellationToken);

    public Task<ProcessOutcome> ExecAsync(string sessionId, SandboxExec exec, CancellationToken cancellationToken = default)
    {
        var args = new List<string>
        {
            "exec", "--session", sessionId, "--as", exec.User,
            "--workdir", DockerRunBuilder.ContainerWorkingDirectory(exec.WorkingDirectory),
            "--timeout", exec.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (name, value) in DockerRunBuilder.FilterEnvironment(exec.Environment))
            args.AddRange(["--env", $"{name}={value}"]);
        args.Add("--");
        args.Add(exec.Program);
        args.AddRange(exec.Arguments);
        return InvokeAsync(args, TimeSpan.FromSeconds(exec.TimeoutSeconds) + ExecGrace, cancellationToken);
    }

    public Task<ProcessOutcome> StopAsync(string sessionId) =>
        InvokeAsync(["stop", "--session", sessionId], Housekeeping, CancellationToken.None);

    public Task<ProcessOutcome> ReapAsync() => InvokeAsync(["reap"], Housekeeping, CancellationToken.None);

    private Task<ProcessOutcome> InvokeAsync(IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
        runner.RunAsync("sudo", ["-n", "-u", SandboxOptions.SandboxUser, options.RunnerPath, .. arguments], timeout, cancellationToken);
}
