namespace CloudAgent.Core.Sandbox;

/// <summary>How the sandbox is driven and the limits its session containers run under.</summary>
/// <param name="RunnerPath">Path of the wrapper script that drives containers as the sandbox user.</param>
/// <param name="Image">The container image sessions run.</param>
/// <param name="Timeout">Longest a single command may run before it is stopped inside the container.</param>
/// <param name="Memory">Memory limit in Docker's notation, such as <c>2g</c>.</param>
/// <param name="Cpus">CPU limit, such as <c>1.5</c>.</param>
/// <param name="TmpSize">Size of the container's <c>/tmp</c>, which holds the package cache and build temp.</param>
/// <param name="MaxSessions">How many sessions may run at once on the host.</param>
/// <param name="IdleTimeout">A session without commands for this long is removed; zero means the default.</param>
/// <param name="MaxLife">A session is removed this long after it started; zero means the default.</param>
public sealed record SandboxOptions(
    string RunnerPath,
    string Image,
    TimeSpan Timeout,
    string Memory = SandboxOptions.DefaultMemory,
    string Cpus = SandboxOptions.DefaultCpus,
    string TmpSize = SandboxOptions.DefaultTmpSize,
    int MaxSessions = SandboxOptions.DefaultMaxSessions,
    TimeSpan IdleTimeout = default,
    TimeSpan MaxLife = default)
{
    public const int DefaultTimeoutSeconds = 1200;
    public const string DefaultMemory = "2g";
    public const string DefaultCpus = "1.5";
    public const string DefaultTmpSize = "1g";
    public const int DefaultMaxSessions = 1;
    public const int MaxCommandSeconds = 1200;

    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultMaxLife = TimeSpan.FromHours(4);

    /// <summary>The internal Docker network session containers join; its only way out is the egress proxy.</summary>
    public const string Network = "cloudagent-sandbox-internal";

    public const string ProxyUrl = "http://cloudagent-egress:3128";
    public const string NoProxy = "localhost,127.0.0.1";
    public const string MountPoint = "/work";
    public const int PidsLimit = 512;
    public const string NamePrefix = "cloudagent-sbx-";

    /// <summary>The user the wrapper script is run as, through sudo.</summary>
    public const string SandboxUser = "cloudagent-sandbox";

    public TimeSpan IdleLimit => IdleTimeout == default ? DefaultIdleTimeout : IdleTimeout;

    public TimeSpan LifeLimit => MaxLife == default ? DefaultMaxLife : MaxLife;
}
