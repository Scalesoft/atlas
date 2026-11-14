using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Ai.Settings.Commands;

/// <summary>
/// What every save of AI settings shares: the WikiAdmin check (the module settings answer), one advisory lock for all AI
/// sections, and the notice to the configuration cache once the transaction has committed.
/// </summary>
public sealed class AiSettingsWrite
{
    /// <summary>The AI settings lock, next to the other administration keys (731145010–731145019, 731145099–100).</summary>
    public const long LockKey = 731145051;
    private readonly ModuleSettings.ModuleSettingsAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly AccessChanges m_afterCommit;
    private readonly IAiConfigurationChanged m_changed;
    private readonly IJobRequests m_jobs;

    public AiSettingsWrite(ModuleSettings.ModuleSettingsAccess access, IAtlasLocks locks, AccessChanges afterCommit, IAiConfigurationChanged changed,
        IJobRequests jobs)
    {
        m_access = access;
        m_locks = locks;
        m_afterCommit = afterCommit;
        m_changed = changed;
        m_jobs = jobs;
    }

    public async Task<UserAccount> BeginAsync(CancellationToken ct)
    {
        var actor = await m_access.RequireWikiAdminAsync(ct);
        await m_locks.AcquireAdvisoryAsync(LockKey, ct);
        // The switch and a profile gate the index backlog: its job (IndexJob, "ai-index.index") plans again once this write commits.
        await m_jobs.RescheduleAsync("ai-index.index", ct);
        return actor;
    }

    /// <summary>Never before the commit (the cache would reload the old rows) and never after a rollback.</summary>
    public void Saved()
    {
        m_afterCommit.AfterCommit(() =>
        {
            m_changed.Changed();
            return Task.CompletedTask;
        });
    }
}
