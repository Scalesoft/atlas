using Atlas.Markdown.Sync;

namespace Atlas.Server.Platform.Identity;

/// <summary>
/// Revocation boundary of live collaboration. Since Atlas.Markdown.Sync 0.3.1 collaboration traffic uses each peer's
/// last authorized access; only a join, the periodic refresh (5 s) and <see cref="DocumentAccessCoordinator"/> refreshes
/// update it. A command that changes who may read or edit content calls <see cref="Changed"/>: after its transaction
/// commits, <see cref="NHibernateTransactionMiddleware"/> awaits <see cref="RefreshAsync"/>, so the command's
/// response follows the boundary. Never refreshed before the commit (a peer would be re-authorized against the old rows)
/// nor after a rollback.
/// </summary>
/// <remarks>
/// Access changes are rare, so every live document is revalidated instead of computing the affected set. Best effort: a
/// failure only logs, and the periodic refresh still applies the change. The response waits at most <see cref="Budget"/>;
/// a slower refresh (each authorization is bounded by the library's AuthorizationTimeout) finishes in the background.
/// Grant validity that simply elapses (StartAt/ExpiresAt) has no commit and is left to the periodic refresh.
/// </remarks>
public sealed class AccessChanges
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private readonly DocumentAccessCoordinator? m_coordinator;
    private readonly ILogger<AccessChanges> m_log;
    private readonly IReadOnlyList<IAccessChangeObserver> m_observers;
    private List<Func<Task>>? m_afterCommit;
    private bool m_changed;

    /// <param name="coordinator">Null in hosts without the collaboration module (some test hosts): nothing to refresh.</param>
    /// <param name="observers">Other live state re-checked after an access change (released secret values, WP4 V5).</param>
    public AccessChanges(ILogger<AccessChanges> log, DocumentAccessCoordinator? coordinator, IEnumerable<IAccessChangeObserver>? observers = null)
    {
        m_log = log;
        m_coordinator = coordinator;
        m_observers = observers?.ToArray() ?? [];
    }

    /// <summary>Runs <paramref name="action"/> after this message's transaction commits (never after a rollback), for live
    /// notifications that must not announce an uncommitted change. Best effort: a failure only logs.</summary>
    public void AfterCommit(Func<Task> action)
    {
        (m_afterCommit ??= []).Add(action);
    }

    /// <summary>Space grants, group membership, account activation/role/security stamp, resource grants or OwnAccess changed.</summary>
    public void Changed()
    {
        m_changed = true;
    }

    internal void Discard()
    {
        m_changed = false;
        m_afterCommit = null;
    }

    internal async Task RefreshAsync()
    {
        var actions = m_afterCommit;
        m_afterCommit = null;
        if (m_changed)
        {
            m_changed = false;
            var refresh = RefreshAllAsync(m_coordinator, m_observers, m_log);
            try
            {
                await refresh.WaitAsync(Budget);
            }
            catch (TimeoutException)
            {
                m_log.LogWarning("Live collaboration access refresh after an access change is still running; the response does not wait for it");
            }
        }
        foreach (var action in actions ?? [])
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                m_log.LogWarning(ex, "A live notification after an access change failed");
            }
        }
    }

    // Never cancelled, not even by the request: a client that disconnects after the commit must not skip the boundary.
    // The collaboration refresh and the other re-checks run side by side, so a slow document pass does not delay them.
    private static Task RefreshAllAsync(DocumentAccessCoordinator? coordinator, IReadOnlyList<IAccessChangeObserver> observers, ILogger log)
    {
        return Task.WhenAll(observers.Select(observer => ObserveAsync(observer, log)).Append(RefreshDocumentsAsync(coordinator, log)));
    }

    private static async Task RefreshDocumentsAsync(DocumentAccessCoordinator? coordinator, ILogger log)
    {
        if (coordinator is null)
        {
            return;
        }
        try
        {
            await coordinator.RefreshAllAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Live collaboration access refresh after an access change failed; the periodic refresh applies it");
        }
    }

    private static async Task ObserveAsync(IAccessChangeObserver observer, ILogger log)
    {
        try
        {
            await observer.AccessChangedAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Live access re-check after an access change failed; the periodic check applies it");
        }
    }
}

/// <summary>Live state outside collaboration that an access change must re-authorize after the commit (AccessChanges).</summary>
public interface IAccessChangeObserver
{
    Task AccessChangedAsync();
}
