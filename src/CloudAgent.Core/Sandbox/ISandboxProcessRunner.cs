namespace CloudAgent.Core.Sandbox;

/// <summary>What a finished (or stopped) process left behind.</summary>
public sealed record ProcessOutcome(int ExitCode, string Output, string Error, bool TimedOut);

/// <summary>Runs a process to completion; the seam that lets the launcher be tested without Docker.</summary>
public interface ISandboxProcessRunner
{
    /// <summary>Run the program with no shell and closed standard input; stop it after <paramref name="timeout"/>.</summary>
    Task<ProcessOutcome> RunAsync(
        string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default);
}
