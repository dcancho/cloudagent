namespace CloudAgent.Core.Sandbox;

/// <summary>One program to run in a disposable container.</summary>
/// <param name="TaskId">Names the container; lowercase letters, digits and hyphens.</param>
/// <param name="Repository">The repository the task directory belongs to, as the wrapper's allowlist knows it.</param>
/// <param name="TaskDirectory">The host directory mounted read-write; the only mount the container gets.</param>
/// <param name="WorkingDirectory">Directory to run in, relative to the task directory; empty for its root.</param>
/// <param name="Program">The program to run, by bare name.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="Environment">Variables the caller would like to pass; only allowlisted names reach the container.</param>
public sealed record SandboxCommand(
    string TaskId,
    string Repository,
    string TaskDirectory,
    string WorkingDirectory,
    string Program,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment = null);
