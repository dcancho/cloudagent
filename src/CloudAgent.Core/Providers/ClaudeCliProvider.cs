using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Providers;

/// <summary>
/// Drives Claude through the local <c>claude</c> CLI in headless mode (<c>claude -p</c>) instead
/// of the HTTP API. When no <c>ANTHROPIC_API_KEY</c> is set, that path is billed against the
/// user's Claude Pro/Max subscription rather than API credits — handy for testing on a plan.
///
/// Note: this is a text-completion bridge. Claude Code's own tools are disabled here so it acts
/// as a pure brain; CloudAgent's tool calling does not flow through this provider. It also spawns
/// the CLI per call, so it's slower than the API — fine for local testing, not for scale.
/// </summary>
public sealed class ClaudeCliProvider : ILlmProvider
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _executable;
    private readonly string? _model;
    private readonly TimeSpan _timeout;

    public ClaudeCliProvider(string? model = null, string? claudePath = null, TimeSpan? timeout = null)
    {
        _executable = ResolveExecutable(claudePath ?? "claude");
        _model = string.IsNullOrWhiteSpace(model) ? null : model;
        _timeout = timeout ?? TimeSpan.FromSeconds(180);
    }

    public string Name => "claude-cli";

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
        };

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");

        var model = request.Model is { Length: > 0 } m ? m : _model;
        if (!string.IsNullOrWhiteSpace(model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
        }

        if (!string.IsNullOrWhiteSpace(request.System))
        {
            psi.ArgumentList.Add("--system-prompt");
            psi.ArgumentList.Add(request.System);
        }

        // Keep it a pure completion — don't let Claude Code act with its own tools. Variadic,
        // so it must come last.
        psi.ArgumentList.Add("--disallowed-tools");
        psi.ArgumentList.Add("Bash");
        psi.ArgumentList.Add("Edit");
        psi.ArgumentList.Add("Write");

        // Ensure subscription auth, not an API key that might be in the environment.
        psi.Environment.Remove("ANTHROPIC_API_KEY");

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start the claude CLI.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        var token = timeoutCts.Token;

        await process.StandardInput.WriteAsync(Render(request.Messages)).ConfigureAwait(false);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);

        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"claude -p timed out after {_timeout.TotalSeconds:0}s.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"claude -p exited {process.ExitCode}: {stderr}");

        return Parse(stdout);
    }

    private static LlmResponse Parse(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        if (root.TryGetProperty("is_error", out var isError) && isError.GetBoolean())
        {
            var reason = root.TryGetProperty("result", out var er) ? er.GetString() : "unknown error";
            throw new InvalidOperationException($"claude -p reported an error: {reason}");
        }

        var text = root.TryGetProperty("result", out var r) ? r.GetString() ?? string.Empty : string.Empty;

        int? inputTokens = null, outputTokens = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it)) inputTokens = it.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt32();
        }

        return new LlmResponse(text, StopReason.EndTurn, inputTokens, outputTokens);
    }

    /// <summary>Render the conversation as a single prompt (Claude Code -p takes one user turn).</summary>
    private static string Render(IReadOnlyList<Message> messages)
    {
        if (messages.Count == 1 && messages[0].Role == Role.User)
            return messages[0].Content;

        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case Role.User:
                    sb.AppendLine($"User: {message.Content}").AppendLine();
                    break;
                case Role.Assistant when !string.IsNullOrWhiteSpace(message.Content):
                    sb.AppendLine($"Assistant: {message.Content}").AppendLine();
                    break;
                case Role.Tool:
                    sb.AppendLine($"[tool result] {message.Content}").AppendLine();
                    break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string ResolveExecutable(string name)
    {
        if (File.Exists(name))
            return Path.GetFullPath(name);

        // Bare name: search PATH (+ Windows executable extensions).
        if (!name.Contains(Path.DirectorySeparatorChar) && !name.Contains(Path.AltDirectorySeparatorChar))
        {
            var extensions = OperatingSystem.IsWindows()
                ? new[] { "", ".exe", ".cmd", ".bat" }
                : new[] { "" };

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(dir, name + ext);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        return name; // let Process.Start surface a clear error if it's genuinely missing
    }
}
