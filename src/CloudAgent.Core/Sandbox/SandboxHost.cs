namespace CloudAgent.Core.Sandbox;

/// <summary>
/// Owns the sandbox on this machine: one session per repository, shared by every conversation working on it,
/// at most <see cref="SandboxOptions.MaxSessions"/> of them with a container at a time (an idle one makes room
/// for another repository, a busy one does not), and the cleanup of containers left behind by an earlier run.
/// </summary>
public sealed class SandboxHost(ISandboxProcessRunner runner, SandboxOptions options, TimeProvider? time = null) : IAsyncDisposable
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromMinutes(1);

    private readonly SandboxWrapperClient _client = new(runner, options);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, SandboxSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<SandboxSession> _running = [];
    private readonly object _gate = new();

    internal SandboxWrapperClient Client => _client;

    internal SandboxOptions Options => options;

    internal DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>The session of a repository, created on first use; its container starts with its first command.</summary>
    public SandboxSession SessionFor(string repository, string taskDirectory)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(repository, out var session))
                _sessions[repository] = session = new SandboxSession(this, repository, taskDirectory);
            return session;
        }
    }

    /// <summary>Remove every sandbox container, including ones an earlier run of the host left.</summary>
    public async Task ReapAsync()
    {
        try
        {
            await _client.ReapAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Console.Error.WriteLine($"[sandbox] could not remove leftover containers: {ex.Message}");
        }
    }

    /// <summary>Remove the containers of sessions that have been idle or alive too long.</summary>
    public async Task ExpireAsync()
    {
        SandboxSession[] sessions;
        lock (_gate)
            sessions = [.. _sessions.Values];
        foreach (var session in sessions)
            await session.StopIfExpiredAsync().ConfigureAwait(false);
    }

    /// <summary>Check for expired sessions every minute until cancelled.</summary>
    public async Task RunMonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MonitorInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await ExpireAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        SandboxSession[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }
        foreach (var session in sessions)
            await session.StopAsync().ConfigureAwait(false);
    }

    internal async Task ClaimAsync(SandboxSession session)
    {
        while (true)
        {
            SandboxSession? idle;
            lock (_gate)
            {
                if (_running.Contains(session) || _running.Count < options.MaxSessions)
                {
                    _running.Add(session);
                    return;
                }
                idle = _running.FirstOrDefault(s => !s.IsBusy);
                if (idle is null)
                    throw new SandboxBusyException(_running.Select(s => s.Repository));
            }
            if (!await idle.TryStopIfIdleAsync().ConfigureAwait(false))
                throw new SandboxBusyException([idle.Repository]);
        }
    }

    internal void Release(SandboxSession session)
    {
        lock (_gate)
            _running.Remove(session);
    }
}
