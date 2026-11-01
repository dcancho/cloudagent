namespace CloudAgent.Core.Sandbox;

/// <summary>
/// Owns the sandbox on this machine: one session per conversation that runs commands, at most
/// <see cref="SandboxOptions.MaxSessions"/> of them with a container at a time, and the cleanup of
/// containers left behind by an earlier run of the host.
/// </summary>
public sealed class SandboxHost(ISandboxProcessRunner runner, SandboxOptions options, TimeProvider? time = null) : IAsyncDisposable
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromMinutes(1);

    private readonly SandboxWrapperClient _client = new(runner, options);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, SandboxSession> _sessions = [];
    private readonly HashSet<SandboxSession> _running = [];
    private readonly object _gate = new();

    internal SandboxWrapperClient Client => _client;

    internal SandboxOptions Options => options;

    internal DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>The session of a conversation, created on first use; its container starts with its first command.</summary>
    public SandboxSession SessionFor(string conversationId, string repository, string taskDirectory)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(conversationId, out var session))
                _sessions[conversationId] = session = new SandboxSession(this, repository, taskDirectory);
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

    /// <summary>End a conversation's session and remove its container.</summary>
    public async Task ReleaseAsync(string conversationId)
    {
        SandboxSession? session;
        lock (_gate)
        {
            if (!_sessions.Remove(conversationId, out session))
                return;
        }
        await session.StopAsync().ConfigureAwait(false);
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

    internal void Claim(SandboxSession session)
    {
        lock (_gate)
        {
            if (!_running.Contains(session) && _running.Count >= options.MaxSessions)
                throw new SandboxBusyException();
            _running.Add(session);
        }
    }

    internal void Release(SandboxSession session)
    {
        lock (_gate)
            _running.Remove(session);
    }
}
