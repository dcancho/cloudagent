namespace CloudAgent.Core.Sandbox;

/// <summary>How a sandboxed command is launched and the limits its container runs under.</summary>
/// <param name="RunnerPath">Path of the wrapper script that starts containers as the sandbox user.</param>
/// <param name="Image">The container image commands run in.</param>
/// <param name="Timeout">Wall-clock limit after which the container is killed.</param>
/// <param name="Memory">Memory limit in Docker's notation, such as <c>2g</c>.</param>
/// <param name="Cpus">CPU limit, such as <c>1.5</c>.</param>
public sealed record SandboxOptions(
    string RunnerPath,
    string Image,
    TimeSpan Timeout,
    string Memory = SandboxOptions.DefaultMemory,
    string Cpus = SandboxOptions.DefaultCpus)
{
    public const int DefaultTimeoutSeconds = 1200;
    public const string DefaultMemory = "2g";
    public const string DefaultCpus = "1.5";

    /// <summary>The internal Docker network sandbox containers join; its only way out is the egress proxy.</summary>
    public const string Network = "cloudagent-sandbox-internal";

    public const string ProxyUrl = "http://cloudagent-egress:3128";
    public const string NoProxy = "localhost,127.0.0.1";
    public const string MountPoint = "/work";
    public const string ContainerUser = "1000:1000";
    public const int PidsLimit = 512;
    public const string NamePrefix = "cloudagent-sbx-";

    /// <summary>The user the wrapper script is run as, through sudo.</summary>
    public const string SandboxUser = "cloudagent-sandbox";
}
