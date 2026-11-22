using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Sandbox;
using CloudAgent.Core.Workspaces;

namespace CloudAgent.Core.Tools.Code;

/// <summary>
/// Runs one allowed program in the workspace and returns its exit code and output. There is no
/// shell: the command is split into a program and arguments, the program must be allowed by the
/// workspace's execution policy, standard input is closed so nothing waits for a prompt, and
/// environment variables that look like secrets are not passed on. With a sandbox launcher the program runs in
/// the conversation's sandbox container instead of on this machine, and the allowed programs are those the image has.
/// </summary>
public sealed class RunCommandTool(IWorkspace workspace, int maxOutputChars = 30_000, ISandboxSession? sandbox = null) : ITool
{
    private const int DefaultTimeoutSeconds = 120;
    private const int MaxTimeoutSeconds = 600;
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(3);
    private static readonly string[] SecretMarkers = ["KEY", "TOKEN", "SECRET", "PASSWORD"];
    private static readonly char[] BatchUnsafe = ['&', '|', '<', '>', '^', '%', '"'];

    public string Name => "run_command";

    public string Description =>
        "Run a program in the workspace, such as a build, the tests, or git, and get its exit code and output. " +
        $"Allowed programs: {Allowed}. " +
        "There is no shell: no pipes, redirection, or chaining with &&; run one command per call.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("command", "The command line, for example: dotnet build or git status --short."),
        ToolParameter.String("directory", "Workspace-relative directory to run in; defaults to the root.", required: false),
        ToolParameter.Integer("timeout_seconds", $"Stop the command after this long; default {DefaultTimeoutSeconds}, at most {MaxTimeoutSeconds}.", required: false),
    ];

    private string Allowed => string.Join(", ", workspace.Execution.AllowedPrograms.Order());

    public async Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> parts;
        try
        {
            parts = CommandLine.Split(input.GetString("command"));
        }
        catch (FormatException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        if (parts.Count == 0)
            return ToolResult.Fail("The command is empty.");

        var program = parts[0];
        if (!workspace.Execution.Allows(program))
            return ToolResult.Fail($"'{program}' is not allowed here. Allowed programs: {Allowed}.");

        var arguments = parts.Skip(1).ToList();
        var relative = input.GetStringOrNull("directory");
        var directory = workspace.Resolve(relative ?? "");
        var seconds = Math.Clamp(input.GetInt("timeout_seconds", DefaultTimeoutSeconds), 1, MaxTimeoutSeconds);

        if (sandbox is not null)
        {
            if (!Directory.Exists(directory))
                return ToolResult.Fail($"Directory not found: {relative}");
            return await RunSandboxedAsync(program, arguments, directory, TimeSpan.FromSeconds(seconds), cancellationToken)
                .ConfigureAwait(false);
        }

        var executable = CommandLine.Find(program);
        if (executable is null)
            return ToolResult.Fail($"'{program}' was not found on this machine.");

        if (IsBatchFile(executable) && arguments.Any(a => a.IndexOfAny(BatchUnsafe) >= 0))
            return ToolResult.Fail($"'{program}' runs through cmd.exe, so its arguments cannot contain & | < > ^ % or quotes.");

        if (!Directory.Exists(directory))
            return ToolResult.Fail($"Directory not found: {relative}");

        return await RunAsync(executable, arguments, directory, TimeSpan.FromSeconds(seconds), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ToolResult> RunSandboxedAsync(
        string program, IReadOnlyList<string> arguments, string directory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var relative = Path.GetRelativePath(workspace.Root, directory);
        var exec = new SandboxExec(
            SandboxUsers.Runner,
            relative == "." ? "" : relative,
            Path.GetFileNameWithoutExtension(program),
            arguments,
            new Dictionary<string, string>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_PAGER"] = "cat",
                ["PAGER"] = "cat",
                ["MSBUILDDISABLENODEREUSE"] = "1",
            },
            (int)Math.Clamp(timeout.TotalSeconds, 1, SandboxOptions.MaxCommandSeconds));

        var watch = Stopwatch.StartNew();
        SandboxResult result;
        try
        {
            result = await sandbox!.ExecAsync(exec, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SandboxBusyException or InvalidOperationException)
        {
            return ToolResult.Fail(ex.Message);
        }

        var text = Cut(string.Join('\n', new[] { result.Output.TrimEnd(), result.Error.TrimEnd() }.Where(s => s.Length > 0)));
        if (result.TimedOut)
            return ToolResult.Fail($"Stopped after {watch.Elapsed.TotalSeconds:0} s without finishing.\n{text}");
        return ToolResult.Ok(
            $"Exit code {result.ExitCode} after {watch.Elapsed.TotalSeconds:0.0} s.\n{(text.Length == 0 ? "(no output)" : text)}");
    }

    private async Task<ToolResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, string directory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
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

        foreach (var name in start.Environment.Keys.ToList())
        {
            if (SecretMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase)))
                start.Environment.Remove(name);
        }
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["PAGER"] = "cat";

        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        var output = new StringBuilder();
        var gate = new object();
        var outputClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Append(string? line, TaskCompletionSource closed)
        {
            if (line is null)
            {
                closed.TrySetResult();
                return;
            }
            lock (gate)
                output.AppendLine(line);
        }
        string Snapshot()
        {
            lock (gate)
                return output.ToString().TrimEnd();
        }

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();
        process.OutputDataReceived += (_, e) => Append(e.Data, outputClosed);
        process.ErrorDataReceived += (_, e) => Append(e.Data, errorClosed);

        var watch = Stopwatch.StartNew();
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var limit = Task.Delay(timeout, cancellationToken);
        if (await Task.WhenAny(exited.Task, limit).ConfigureAwait(false) != exited.Task)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            return ToolResult.Fail($"Stopped after {timeout.TotalSeconds:0} s without finishing.\n{Cut(Snapshot())}");
        }

        // A program can leave a background process holding its output open, such as a build server;
        // the output that matters has arrived by the time it exits, so the wait for the rest is short.
        await Task.WhenAny(Task.WhenAll(outputClosed.Task, errorClosed.Task), Task.Delay(OutputGrace, CancellationToken.None))
            .ConfigureAwait(false);
        var text = Cut(Snapshot());
        return ToolResult.Ok(
            $"Exit code {process.ExitCode} after {watch.Elapsed.TotalSeconds:0.0} s.\n{(text.Length == 0 ? "(no output)" : text)}");
    }

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

    private static bool IsBatchFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cmd" or ".bat";

    private string Cut(string text)
    {
        if (text.Length <= maxOutputChars)
            return text;

        var head = maxOutputChars / 3;
        var tail = maxOutputChars - head;
        return $"{text[..head]}\n... [{text.Length - maxOutputChars} characters cut] ...\n{text[^tail..]}";
    }
}
