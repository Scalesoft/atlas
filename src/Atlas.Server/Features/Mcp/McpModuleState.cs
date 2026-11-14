using Atlas.Server.Features.Mcp.Queries;

namespace Atlas.Server.Features.Mcp;

/// <summary>
/// Whether the MCP module is enabled, for the gate in front of /mcp and the token profile. Process memory: a save in this process
/// invalidates it at once (McpEndpoints, after the commit), otherwise it is read again after <see cref="Lifetime"/>. Never stored
/// means off (O2). The read uses its own session: the gate runs before authentication and outside any message transaction.
/// </summary>
public sealed class McpModuleState
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly AtlasDatabase m_database;
    private readonly TimeProvider m_clock;
    private Snapshot m_current = new(false, DateTimeOffset.MinValue);

    public McpModuleState(AtlasDatabase database, TimeProvider clock)
    {
        m_database = database;
        m_clock = clock;
    }

    public async ValueTask<bool> IsEnabledAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var current = Volatile.Read(ref m_current);
            var now = m_clock.GetUtcNow();
            if (now - current.ReadAt < Lifetime) return current.Enabled;
            bool enabled;
            using (var session = m_database.OpenSession())
            using (var transaction = session.BeginTransaction())
            {
                enabled = (await CurrentMcp.LoadAsync(session, ct))?.Enabled ?? false;
                await transaction.CommitAsync(ct);
            }
            // An invalidation replaces the snapshot identity, even when no value was cached.
            // Never publish or return a read that crossed a committed settings change.
            if (ReferenceEquals(Interlocked.CompareExchange(ref m_current, new Snapshot(enabled, now), current), current))
                return enabled;
        }
    }

    /// <summary>Forget the cached state; call after the transaction that changed it has committed.</summary>
    public void Invalidate() => Volatile.Write(ref m_current, new Snapshot(false, DateTimeOffset.MinValue));

    private sealed record Snapshot(bool Enabled, DateTimeOffset ReadAt);
}
