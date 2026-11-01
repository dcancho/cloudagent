using System.Globalization;
using System.Text.RegularExpressions;

namespace CloudAgent.Core.Sandbox;

/// <summary>
/// Builds the docker arguments that drive a session container. The container has no capabilities, a
/// read-only root, one writable mount, a limited life, and a network whose only exit is the egress proxy.
/// The wrapper script on the VM rebuilds the same lists from validated parts, so this is also the
/// reference the script is tested against.
/// </summary>
public static partial class DockerRunBuilder
{
    /// <summary>The variables a caller may pass into the container; anything else is dropped.</summary>
    public static readonly IReadOnlyList<string> AllowedEnvironment =
        ["CI", "GIT_PAGER", "GIT_TERMINAL_PROMPT", "MSBUILDDISABLENODEREUSE", "PAGER", "TERM"];

    private static readonly (string Name, string Value)[] FixedEnvironment =
    [
        ("HOME", "/tmp"),
        ("DOTNET_CLI_HOME", "/tmp"),
        ("NUGET_PACKAGES", "/tmp/.nuget/packages"),
        ("DOTNET_CLI_TELEMETRY_OPTOUT", "1"),
        ("DOTNET_NOLOGO", "1"),
        ("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1"),
        ("HTTP_PROXY", SandboxOptions.ProxyUrl),
        ("HTTPS_PROXY", SandboxOptions.ProxyUrl),
        ("NO_PROXY", SandboxOptions.NoProxy),
        ("http_proxy", SandboxOptions.ProxyUrl),
        ("https_proxy", SandboxOptions.ProxyUrl),
        ("no_proxy", SandboxOptions.NoProxy),
    ];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{2,39}$")]
    private static partial Regex SessionIdPattern();

    public static bool IsValidSessionId(string sessionId) => SessionIdPattern().IsMatch(sessionId);

    /// <summary>The container's name, which the other docker commands use.</summary>
    public static string ContainerName(string sessionId) => SandboxOptions.NamePrefix + sessionId;

    /// <summary>The caller's variables that may enter the container, in a fixed order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> FilterEnvironment(IReadOnlyDictionary<string, string>? environment) =>
        (environment ?? new Dictionary<string, string>())
            .Where(pair => AllowedEnvironment.Contains(pair.Key, StringComparer.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

    /// <summary>The path to work in inside the container for a task-relative directory.</summary>
    /// <exception cref="ArgumentException">The directory is absolute or leaves the task directory.</exception>
    public static string ContainerWorkingDirectory(string relative)
    {
        var path = relative.Replace('\\', '/').Trim('/');
        if (path.Length == 0 || path == ".")
            return SandboxOptions.MountPoint;
        if (relative.StartsWith('/') || path.Split('/').Any(part => part == ".."))
            throw new ArgumentException($"'{relative}' is outside the task directory.", nameof(relative));
        return $"{SandboxOptions.MountPoint}/{path}";
    }

    /// <summary>The arguments after <c>docker</c> that start a session container, detached.</summary>
    /// <exception cref="ArgumentException">The session id is not acceptable.</exception>
    public static IReadOnlyList<string> BuildStart(SandboxStart start, SandboxOptions options)
    {
        RequireValid(start.SessionId);
        var args = new List<string>
        {
            "run", "-d", "--rm", "--init",
            "--pull", "never",
            "--name", ContainerName(start.SessionId),
            "--network", SandboxOptions.Network,
            "--cap-drop=ALL",
            "--security-opt", "no-new-privileges",
            "--pids-limit", SandboxOptions.PidsLimit.ToString(CultureInfo.InvariantCulture),
            "--memory", options.Memory,
            "--cpus", options.Cpus,
            "--read-only",
            "--tmpfs", $"/tmp:rw,exec,nosuid,size={options.TmpSize}",
            "--user", SandboxUsers.ContainerUser(SandboxUsers.Runner),
            "-v", $"{start.TaskDirectory}:{SandboxOptions.MountPoint}:rw",
            "-w", SandboxOptions.MountPoint,
        };
        foreach (var (name, value) in FixedEnvironment)
            args.AddRange(["-e", $"{name}={value}"]);
        args.AddRange(["--entrypoint", "timeout", options.Image,
            ((long)options.LifeLimit.TotalSeconds).ToString(CultureInfo.InvariantCulture), "sleep", "infinity"]);
        return args;
    }

    /// <summary>The arguments after <c>docker</c> that run a command in a session, stopped by <c>timeout</c> inside.</summary>
    /// <exception cref="ArgumentException">The session id, user or working directory is not acceptable.</exception>
    public static IReadOnlyList<string> BuildExec(string sessionId, SandboxExec exec)
    {
        RequireValid(sessionId);
        var args = new List<string>
        {
            "exec",
            "-u", SandboxUsers.ContainerUser(exec.User),
            "-w", ContainerWorkingDirectory(exec.WorkingDirectory),
        };
        foreach (var (name, value) in FilterEnvironment(exec.Environment))
            args.AddRange(["-e", $"{name}={value}"]);
        args.AddRange([ContainerName(sessionId), "timeout", "-k", "10",
            exec.TimeoutSeconds.ToString(CultureInfo.InvariantCulture), exec.Program]);
        args.AddRange(exec.Arguments);
        return args;
    }

    /// <summary>The arguments after <c>docker</c> that remove a session container.</summary>
    public static IReadOnlyList<string> BuildStop(string sessionId)
    {
        RequireValid(sessionId);
        return ["rm", "-f", ContainerName(sessionId)];
    }

    private static void RequireValid(string sessionId)
    {
        if (!IsValidSessionId(sessionId))
            throw new ArgumentException($"'{sessionId}' is not a valid session id.", nameof(sessionId));
    }
}
