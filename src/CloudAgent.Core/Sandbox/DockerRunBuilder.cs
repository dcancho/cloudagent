using System.Text.RegularExpressions;

namespace CloudAgent.Core.Sandbox;

/// <summary>
/// Builds the <c>docker run</c> arguments for one sandboxed command. The container has no capabilities, a
/// read-only root, one writable mount, and a network whose only exit is the egress proxy. The wrapper
/// script on the VM rebuilds the same list from validated parts, so this is also the reference the script is
/// tested against.
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
    private static partial Regex TaskIdPattern();

    public static bool IsValidTaskId(string taskId) => TaskIdPattern().IsMatch(taskId);

    /// <summary>The container's name, which <c>docker kill</c> uses.</summary>
    public static string ContainerName(string taskId) => SandboxOptions.NamePrefix + taskId;

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

    /// <summary>The arguments after <c>docker</c>, starting with <c>run</c>.</summary>
    /// <exception cref="ArgumentException">The task id or the working directory is not acceptable.</exception>
    public static IReadOnlyList<string> Build(SandboxCommand command, SandboxOptions options)
    {
        if (!IsValidTaskId(command.TaskId))
            throw new ArgumentException($"'{command.TaskId}' is not a valid task id.", nameof(command));

        var args = new List<string>
        {
            "run",
            "--rm",
            "--pull", "never",
            "--name", ContainerName(command.TaskId),
            "--network", SandboxOptions.Network,
            "--cap-drop=ALL",
            "--security-opt", "no-new-privileges",
            "--pids-limit", SandboxOptions.PidsLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--memory", options.Memory,
            "--cpus", options.Cpus,
            "--read-only",
            "--tmpfs", "/tmp:rw,exec,nosuid,size=512m",
            "--user", SandboxOptions.ContainerUser,
            "-v", $"{command.TaskDirectory}:{SandboxOptions.MountPoint}:rw",
            "-w", ContainerWorkingDirectory(command.WorkingDirectory),
        };

        foreach (var (name, value) in FixedEnvironment)
            args.AddRange(["-e", $"{name}={value}"]);
        foreach (var (name, value) in FilterEnvironment(command.Environment))
            args.AddRange(["-e", $"{name}={value}"]);

        args.Add(options.Image);
        args.Add(command.Program);
        args.AddRange(command.Arguments);
        return args;
    }
}
