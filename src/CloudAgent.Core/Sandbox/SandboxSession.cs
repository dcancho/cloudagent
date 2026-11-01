using System.Globalization;

namespace CloudAgent.Core.Sandbox;

/// <summary>
/// One repository's container, shared by the conversations working on it. It starts on the first command, serves commands one at a time so installed
/// packages and build output persist, and is removed when it has been idle too long, has lived too long, or
/// is stopped. The task directory is never removed.
/// </summary>
public sealed class SandboxSession : ISandboxSession
{
    private readonly SandboxHost _host;
    private readonly string _repository;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _id;
    private DateTimeOffset _started;
    private DateTimeOffset _lastUsed;

    internal SandboxSession(SandboxHost host, string repository, string taskDirectory)
    {
        _host = host;
        _repository = repository;
        _directory = taskDirectory;
    }

    /// <summary>The repository this session works on.</summary>
    public string Repository => _repository;

    /// <summary>Whether a command is running or waiting to start.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Whether a container is currently held for this session.</summary>
    public bool IsRunning => _id is not null;

    public async Task<SandboxResult> ExecAsync(SandboxExec exec, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
                ProcessOutcome outcome;
                try
                {
                    outcome = await _host.Client.ExecAsync(_id!, exec, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await StopAsync().ConfigureAwait(false);
                    throw;
                }
                _lastUsed = _host.Now;

                if (outcome.TimedOut)
                {
                    await StopAsync().ConfigureAwait(false);
                    return new SandboxResult(outcome.ExitCode, outcome.Output, outcome.Error, TimedOut: true);
                }
                if (attempt == 0 && Vanished(outcome))
                {
                    await StopAsync().ConfigureAwait(false);
                    continue;
                }
                return new SandboxResult(outcome.ExitCode, outcome.Output, outcome.Error, outcome.ExitCode == 124);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Remove the container now; a later command starts a new one.</summary>
    public async Task StopAsync()
    {
        var id = Interlocked.Exchange(ref _id, null);
        if (id is null)
            return;

        _host.Release(this);
        try
        {
            await _host.Client.StopAsync(id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }
    }

    internal async Task StopIfExpiredAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
            return;
        try
        {
            if (_id is not null && Expired())
                await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<bool> TryStopIfIdleAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
            return false;
        try
        {
            await StopAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool Expired() =>
        _host.Now - _lastUsed >= _host.Options.IdleLimit || _host.Now - _started >= _host.Options.LifeLimit;

    private static bool Vanished(ProcessOutcome outcome) =>
        outcome.ExitCode != 0
        && (outcome.Error.Contains("No such container", StringComparison.Ordinal)
            || outcome.Error.Contains("is not running", StringComparison.Ordinal));

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_id is not null && Expired())
            await StopAsync().ConfigureAwait(false);
        if (_id is not null)
            return;

        await _host.ClaimAsync(this).ConfigureAwait(false);
        var id = "s-" + Guid.NewGuid().ToString("N")[..12];
        ProcessOutcome outcome;
        try
        {
            outcome = await _host.Client.StartAsync(new SandboxStart(id, _repository, _directory), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            _host.Release(this);
            throw;
        }

        if (outcome.ExitCode != 0 || outcome.TimedOut)
        {
            _host.Release(this);
            try
            {
                await _host.Client.StopAsync(id).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
            }
            var reason = outcome.Error.Trim().Length > 0 ? outcome.Error.Trim() : $"exit code {outcome.ExitCode.ToString(CultureInfo.InvariantCulture)}";
            throw new InvalidOperationException($"The sandbox could not start: {reason}");
        }

        _id = id;
        _started = _lastUsed = _host.Now;
    }
}
