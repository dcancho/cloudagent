namespace CloudAgent.Core.Sandbox;

/// <summary>What a sandboxed command produced.</summary>
/// <param name="TaskDirectory">The directory the container worked in; it is never removed.</param>
public sealed record SandboxResult(int ExitCode, string Output, string Error, bool TimedOut, string TaskDirectory);

/// <summary>
/// Runs commands through the wrapper script, which starts one disposable container per command under the
/// sandbox user. A command that outlives its time limit has its container killed by name; the task directory
/// is left as it is.
/// </summary>
public sealed class SandboxLauncher(ISandboxProcessRunner runner, SandboxOptions options, string repository)
{
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(30);

    public SandboxOptions Options => options;

    /// <summary>The repository this launcher starts containers for.</summary>
    public string Repository => repository;

    /// <summary>Run <paramref name="command"/> and wait for it, at most <paramref name="timeout"/> (or the configured limit if shorter).</summary>
    public async Task<SandboxResult> RunAsync(SandboxCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!DockerRunBuilder.IsValidTaskId(command.TaskId))
            throw new ArgumentException($"'{command.TaskId}' is not a valid task id.", nameof(command));

        var limit = timeout < options.Timeout ? timeout : options.Timeout;
        var (executable, prefix) = Invocation();
        var arguments = prefix.Concat(RunArguments(command)).ToList();

        ProcessOutcome outcome;
        try
        {
            outcome = await runner.RunAsync(executable, arguments, limit, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillAsync(command.TaskId).ConfigureAwait(false);
            throw;
        }

        if (outcome.TimedOut)
            await KillAsync(command.TaskId).ConfigureAwait(false);

        return new SandboxResult(outcome.ExitCode, outcome.Output, outcome.Error, outcome.TimedOut, command.TaskDirectory);
    }

    private async Task KillAsync(string taskId)
    {
        var (executable, prefix) = Invocation();
        try
        {
            await runner.RunAsync(executable, [.. prefix, "kill", "--task", taskId], KillTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }
    }

    private (string Executable, string[] Prefix) Invocation() =>
        ("sudo", ["-n", "-u", SandboxOptions.SandboxUser, options.RunnerPath]);

    private List<string> RunArguments(SandboxCommand command)
    {
        var args = new List<string>
        {
            "run",
            "--task", command.TaskId,
            "--repo", repository,
            "--image", options.Image,
            "--memory", options.Memory,
            "--cpus", options.Cpus,
            "--workdir", DockerRunBuilder.ContainerWorkingDirectory(command.WorkingDirectory),
        };
        foreach (var (name, value) in DockerRunBuilder.FilterEnvironment(command.Environment))
            args.AddRange(["--env", $"{name}={value}"]);
        args.Add("--");
        args.Add(command.Program);
        args.AddRange(command.Arguments);
        return args;
    }
}
