using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Server.Tests.PageGate;

/// <summary>
/// The real locks, with every call written down with its argument: what the text of a statement does not say and LockTrace
/// therefore cannot see (which advisory key, which space, which account, which page). A test may also have something
/// committed just before the next space row lock, as PageGateMovedPageTests does to move a page under a gate.
/// </summary>
internal sealed class RecordingLocks : IAtlasLocks
{
    // On a host shared by tests (Register): the log of the test in whose flow the call is made.
    private static readonly AsyncLocal<Log?> s_current = new();

    private readonly IAtlasLocks m_inner;
    private readonly Log? m_own;

    private RecordingLocks(IAtlasLocks inner, Log? log)
    {
        m_inner = inner;
        m_own = log;
    }

    // Without a log of its test a call is written into a throwaway one.
    private Log m_log => m_own ?? s_current.Value ?? new Log();

    /// <summary>Registers the recording locks of every scope of a host over one log.</summary>
    public static Action<IServiceCollection> Into(Log log)
    {
        return services => services.AddScoped<IAtlasLocks>(provider => new RecordingLocks(
            new AtlasLocks(provider.GetRequiredService<NHibernate.ISession>(), provider.GetRequiredService<TransactionIntent>()), log));
    }

    /// <summary>Registers the recording locks on a host that tests share: each call goes to the log of the test that started one
    /// in its flow (<see cref="Start"/>), and a test that started none sees the real locks alone.</summary>
    public static void Register(IServiceCollection services)
    {
        services.AddScoped<IAtlasLocks>(provider => new RecordingLocks(
            new AtlasLocks(provider.GetRequiredService<NHibernate.ISession>(), provider.GetRequiredService<TransactionIntent>()), null));
    }

    /// <summary>A log for the calling test's flow; call it in the test method itself, so it flows into the messages it sends.</summary>
    public static Log Start()
    {
        return s_current.Value = new Log();
    }

    public Task AcquireContentSharedAsync(CancellationToken ct)
    {
        m_log.Add("content shared");
        return m_inner.AcquireContentSharedAsync(ct);
    }

    public Task AcquireContentExclusiveAsync(CancellationToken ct)
    {
        m_log.Add("content exclusive");
        return m_inner.AcquireContentExclusiveAsync(ct);
    }

    public Task AcquireAdvisoryAsync(long key, CancellationToken ct)
    {
        m_log.Add($"advisory {key}");
        return m_inner.AcquireAdvisoryAsync(key, ct);
    }

    public Task AcquireAdvisoryAsync(string key, CancellationToken ct)
    {
        m_log.Add($"advisory {key}");
        return m_inner.AcquireAdvisoryAsync(key, ct);
    }

    public async Task LockSpaceSharedAsync(Guid id, CancellationToken ct)
    {
        await m_log.BeforeSpaceLockAsync();
        m_log.Add($"space {id} FOR SHARE");
        await m_inner.LockSpaceSharedAsync(id, ct);
    }

    public Features.Pages.Domain.PublicationMode? SharedSpacePublicationMode(Guid id) => m_inner.SharedSpacePublicationMode(id);

    public async Task<bool> LockSpaceForUpdateAsync(Guid id, CancellationToken ct)
    {
        await m_log.BeforeSpaceLockAsync();
        m_log.Add($"space {id} FOR UPDATE");
        return await m_inner.LockSpaceForUpdateAsync(id, ct);
    }

    public Task<bool> LockPageSharedAsync(Guid id, CancellationToken ct)
    {
        m_log.Add($"page {id} FOR SHARE, lock only");
        return m_inner.LockPageSharedAsync(id, ct);
    }

    public Task<bool> LockPageForUpdateAsync(Guid id, CancellationToken ct)
    {
        m_log.Add($"page {id} FOR UPDATE, lock only");
        return m_inner.LockPageForUpdateAsync(id, ct);
    }

    public Task<Page?> ReadPageSharedAsync(Guid id, CancellationToken ct)
    {
        m_log.Add($"page {id} FOR SHARE");
        return m_inner.ReadPageSharedAsync(id, ct);
    }

    public Task LockAccountSharedAsync(Guid id, CancellationToken ct)
    {
        m_log.Add($"account {id} FOR SHARE, lock only");
        return m_inner.LockAccountSharedAsync(id, ct);
    }

    public Task<UserAccount?> ReadAccountSharedAsync(Guid id, CancellationToken ct)
    {
        m_log.Add($"account {id} FOR SHARE");
        return m_inner.ReadAccountSharedAsync(id, ct);
    }

    public Task<bool> LockAccountForUpdateAsync(Guid id, CancellationToken ct)
    {
        m_log.Add($"account {id} FOR UPDATE");
        return m_inner.LockAccountForUpdateAsync(id, ct);
    }

    public Task SetActorAsync(Guid id, CancellationToken ct, Guid? browserSessionId = null)
    {
        m_log.Add($"actor {id}");
        return m_inner.SetActorAsync(id, ct, browserSessionId);
    }

    public Task<(long Version, int ProxyHopLimit)> ReadSecurityConfigurationSharedAsync(CancellationToken ct)
    {
        m_log.Add("security configuration FOR SHARE");
        return m_inner.ReadSecurityConfigurationSharedAsync(ct);
    }

    public Task<IReadOnlyList<Guid>> LockGroupsForKeyShareAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        m_log.Add($"groups {string.Join(",", ids)} FOR KEY SHARE");
        return m_inner.LockGroupsForKeyShareAsync(ids, ct);
    }

    public Task<IReadOnlyList<Guid>> LockPagesForUpdateAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        m_log.Add($"pages {string.Join(",", ids)} FOR UPDATE");
        return m_inner.LockPagesForUpdateAsync(ids, ct);
    }

    /// <summary>The calls of every scope of one host, in order, and the action armed for the next space row lock.</summary>
    public sealed class Log
    {
        private readonly List<string> m_calls = [];
        private Func<Task>? m_beforeSpaceLock;

        public IReadOnlyList<string> Calls => m_calls.ToArray();

        public void Clear()
        {
            m_calls.Clear();
        }

        /// <summary>Runs once, just before the next space row lock any transaction takes: the window in which a page can move
        /// between a gate's look at its space and that lock.</summary>
        public void BeforeNextSpaceLock(Func<Task> action)
        {
            m_beforeSpaceLock = action;
        }

        internal void Add(string call)
        {
            m_calls.Add(call);
        }

        internal async Task BeforeSpaceLockAsync()
        {
            if (Interlocked.Exchange(ref m_beforeSpaceLock, null) is { } action)
            {
                await action();
            }
        }
    }
}
