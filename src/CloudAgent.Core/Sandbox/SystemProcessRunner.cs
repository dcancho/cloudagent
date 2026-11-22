using System.Diagnostics;
using System.Text;

namespace CloudAgent.Core.Sandbox;

/// <summary>Runs real processes.</summary>
public sealed class SystemProcessRunner : ISandboxProcessRunner
{
    public async Task<ProcessOutcome> RunAsync(
        string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        process.Start();
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessOutcome(-1, await Collect(output), await Collect(error), TimedOut: true);
        }

        return new ProcessOutcome(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false), TimedOut: false);
    }

    private static async Task<string> Collect(Task<string> read) =>
        await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false) == read
            ? await read.ConfigureAwait(false)
            : "";

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
