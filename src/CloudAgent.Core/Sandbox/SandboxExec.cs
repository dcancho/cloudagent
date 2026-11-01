namespace CloudAgent.Core.Sandbox;

/// <summary>The users commands can run as inside a session container.</summary>
public static class SandboxUsers
{
    /// <summary>Runs the conversation agent's commands, uid 1000.</summary>
    public const string Runner = "runner";

    /// <summary>The container uid for <paramref name="user"/>.</summary>
    /// <exception cref="ArgumentException">The user is not one the sandbox knows.</exception>
    public static string ContainerUser(string user) =>
        user == Runner ? "1000:1000" : throw new ArgumentException($"'{user}' is not a sandbox user.", nameof(user));
}

/// <summary>One program to run in a session container.</summary>
/// <param name="User">Who runs it, one of <see cref="SandboxUsers"/>.</param>
/// <param name="WorkingDirectory">Directory to run in, relative to the task directory; empty for its root.</param>
/// <param name="Program">The program, by bare name.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="Environment">Variables to pass; only allowlisted names reach the container.</param>
/// <param name="TimeoutSeconds">Limit enforced inside the container with <c>timeout</c>.</param>
public sealed record SandboxExec(
    string User,
    string WorkingDirectory,
    string Program,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment = null,
    int TimeoutSeconds = 120);

/// <summary>What a session container needs to start.</summary>
/// <param name="SessionId">Names the container; lowercase letters, digits and hyphens.</param>
/// <param name="Repository">The repository as the wrapper's allowlist knows it.</param>
/// <param name="TaskDirectory">The host directory mounted read-write; the only mount.</param>
public sealed record SandboxStart(string SessionId, string Repository, string TaskDirectory);

/// <summary>What a sandboxed command produced.</summary>
/// <param name="TimedOut">The command hit its time limit and was stopped.</param>
public sealed record SandboxResult(int ExitCode, string Output, string Error, bool TimedOut);

/// <summary>Runs commands in a sandbox session; the seam step 2 extends with another user.</summary>
public interface ISandboxSession
{
    /// <exception cref="SandboxBusyException">Another conversation holds the sandbox.</exception>
    Task<SandboxResult> ExecAsync(SandboxExec exec, CancellationToken cancellationToken = default);
}

/// <summary>The sandbox has no room for another session.</summary>
public sealed class SandboxBusyException()
    : Exception("The sandbox is in use by another conversation. Try again when that one has finished.");
