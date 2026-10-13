namespace DbDataBuild.Execution;

/// <summary>
/// `max_parallel` of the connections of a project: how many sessions a refresh may have open at one connection at once. A connection with no limit is unlimited. The count is of sessions a run opens to read rows at an
/// origin (a transfer of a copy, the checks and `source` resolvers that go with it). The destination is limited by the number of loads that run at once (the refresh takes the smaller of `parallel` and the cap, see <see cref="CapOf"/>). A limit holds within one run: another process has its own.
/// </summary>
public sealed class SessionLimits
{
    private readonly Dictionary<string, SemaphoreSlim> gates;
    private readonly Dictionary<string, int> caps;

    public SessionLimits(IReadOnlyDictionary<string, int> limits)
    {
        caps = limits.Where(c => c.Value > 0).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
        gates = caps.ToDictionary(c => c.Key, c => new SemaphoreSlim(c.Value, c.Value), StringComparer.Ordinal);
    }

    public static SessionLimits None { get; } = new(new Dictionary<string, int>());

    /// <summary>The cap of a connection, or null when it has none.</summary>
    public int? CapOf(string connection) => caps.TryGetValue(connection, out var cap) ? cap : null;

    private readonly Dictionary<string, int> inUse = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> mostInUse = new(StringComparer.Ordinal);

    /// <summary>The most places of a connection that were ever held at once, capped or not (a test proves that the cap held, and that without it there was something to hold back).</summary>
    public int MostInUse(string connection) { lock (inUse) return mostInUse.GetValueOrDefault(connection); }

    /// <summary>Waits for a place at the connection and returns it; disposing the lease gives it back. Without a cap there is nothing to wait for, but the use is still counted.</summary>
    public async Task<IDisposable> AcquireAsync(string connection, CancellationToken ct)
    {
        gates.TryGetValue(connection, out var gate);
        if (gate != null) await gate.WaitAsync(ct);
        lock (inUse)
        {
            var now = inUse[connection] = inUse.GetValueOrDefault(connection) + 1;
            if (now > mostInUse.GetValueOrDefault(connection)) mostInUse[connection] = now;
        }
        return new Lease(this, connection, gate);
    }

    /// <summary>Opens a read session at a connection once a place is free; the place is held for as long as the session is open.</summary>
    public async Task<ReadSession> OpenAsync(string connection, Func<Task<ReadSession>> open, CancellationToken ct)
    {
        var lease = await AcquireAsync(connection, ct);
        try
        {
            var session = await open();
            session.HoldLease(lease);
            return session;
        }
        catch { lease.Dispose(); throw; }
    }

    private sealed class Lease(SessionLimits owner, string connection, SemaphoreSlim? gate) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 1) return;
            lock (owner.inUse) owner.inUse[connection]--;
            gate?.Release();
        }
    }
}
