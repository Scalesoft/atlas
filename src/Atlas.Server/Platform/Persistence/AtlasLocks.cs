using NHibernate;
using Atlas.Server.Features.Pages.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Persistence;

public sealed class AtlasLocks : IAtlasLocks
{
    private const long ContentLockKey = 731145012;
    private readonly ISession m_session;
    private readonly TransactionIntent? m_intent;
    // Locks the current transaction already holds. Taking one again is a no-op in PostgreSQL but still a round-trip,
    // and the prologue of a request reaches the content lock and its space through more than one caller.
    private ITransaction? m_heldIn;
    private bool m_contentShared;
    // The spaces held FOR SHARE in this transaction, with the „Publikace“ default read by the same lock.
    private readonly Dictionary<Guid, PublicationMode> m_sharedSpaces = [];

    public AtlasLocks(ISession session, TransactionIntent? intent = null)
    {
        m_session = session;
        m_intent = intent;
    }

    public async Task AcquireContentSharedAsync(CancellationToken ct)
    {
        RequireTransaction();
        ForgetLocksOfEndedTransaction();
        if (m_contentShared)
        {
            return;
        }
        await m_session.CreateSQLQuery("SELECT pg_advisory_xact_lock_shared(:key)")
            .SetParameter("key", ContentLockKey)
            .ExecuteUpdateAsync(ct);
        m_contentShared = true;
    }

    public Task AcquireContentExclusiveAsync(CancellationToken ct)
    {
        return AcquireAdvisoryAsync(ContentLockKey, ct);
    }

    public async Task AcquireAdvisoryAsync(long key, CancellationToken ct)
    {
        RequireTransaction();
        await m_session.CreateSQLQuery("SELECT pg_advisory_xact_lock(:key)")
            .SetParameter("key", key)
            .ExecuteUpdateAsync(ct);
    }

    public async Task AcquireAdvisoryAsync(string textKey, CancellationToken ct)
    {
        RequireTransaction();
        await m_session.CreateSQLQuery("SELECT pg_advisory_xact_lock(hashtextextended(:key, 812))")
            .SetParameter("key", textKey)
            .ExecuteUpdateAsync(ct);
    }

    public async Task LockSpaceSharedAsync(Guid spaceId, CancellationToken ct)
    {
        RequireTransaction();
        ForgetLocksOfEndedTransaction();
        if (m_sharedSpaces.ContainsKey(spaceId))
        {
            return;
        }
        var mode = await m_session.CreateSQLQuery("SELECT \"PublicationMode\" FROM \"Space\" WHERE \"Id\"=:id FOR SHARE")
            .SetParameter("id", spaceId)
            .UniqueResultAsync<string?>(ct);
        if (mode is null)
        {
            throw new ApiException(404, "Prostor nebyl nalezen nebo k němu nemáte přístup.");
        }
        m_sharedSpaces[spaceId] = Enum.Parse<PublicationMode>(mode);
    }

    public PublicationMode? SharedSpacePublicationMode(Guid spaceId)
    {
        ForgetLocksOfEndedTransaction();
        return m_sharedSpaces.TryGetValue(spaceId, out var mode) ? mode : null;
    }

    public async Task<bool> LockSpaceForUpdateAsync(Guid spaceId, CancellationToken ct)
    {
        return await LockRowAsync("SELECT \"Id\" FROM \"Space\" WHERE \"Id\"=:id FOR UPDATE", spaceId, ct);
    }

    public async Task<bool> LockPageSharedAsync(Guid pageId, CancellationToken ct)
    {
        return await LockRowAsync("SELECT \"Id\" FROM \"Page\" WHERE \"Id\"=:id FOR SHARE", pageId, ct);
    }

    public async Task<bool> LockPageForUpdateAsync(Guid pageId, CancellationToken ct)
    {
        return await LockRowAsync("SELECT \"Id\" FROM \"Page\" WHERE \"Id\"=:id FOR UPDATE", pageId, ct);
    }

    public Task<Page?> ReadPageSharedAsync(Guid pageId, CancellationToken ct)
    {
        return ReadSharedAsync<Page>("Page", pageId, ct);
    }

    public Task<UserAccount?> ReadAccountSharedAsync(Guid userId, CancellationToken ct)
    {
        return ReadSharedAsync<UserAccount>("UserAccount", userId, ct);
    }

    public async Task LockAccountSharedAsync(Guid userId, CancellationToken ct)
    {
        RequireTransaction();
        await m_session.CreateSQLQuery("SELECT \"Id\" FROM \"UserAccount\" WHERE \"Id\"=:id FOR SHARE")
            .SetParameter("id", userId)
            .UniqueResultAsync(ct);
    }

    public async Task<bool> LockAccountForUpdateAsync(Guid userId, CancellationToken ct)
    {
        return await LockRowAsync("SELECT \"Id\" FROM \"UserAccount\" WHERE \"Id\"=:id FOR UPDATE", userId, ct);
    }

    public async Task SetActorAsync(Guid actorId, CancellationToken ct, Guid? browserSessionId = null)
    {
        RequireTransaction();
        // Every content read passes here, so a read's non-durable commit costs no extra round-trip (TransactionIntent).
        // This statement runs after the account lock is acquired. Combining the revocation read with SELECT FOR SHARE would
        // instead keep the snapshot taken before a lock wait, potentially missing the logout that held the lock.
        var rows = await m_session.CreateSQLQuery("SELECT " + (m_intent?.IsRead == true ? "set_config('synchronous_commit','off',true)" : "1")
                + " WHERE NOT EXISTS (SELECT 1 FROM \"RevokedBrowserSession\" WHERE \"Id\" = :session)")
            .SetParameter("session", browserSessionId, NHibernateUtil.Guid)
            .ListAsync(ct);
        if (rows.Count == 0)
        {
            throw new ApiException(401, "Přihlášení vypršelo.");
        }
        // The history rows of pages and spaces take the actor from here (ContentRowEffects); nothing reads a session variable any more.
        TransactionActor.Set(m_session, actorId);
    }

    public async Task<(long Version, int ProxyHopLimit)> ReadSecurityConfigurationSharedAsync(CancellationToken ct)
    {
        RequireTransaction();
        var row = await m_session.CreateSQLQuery("SELECT \"Version\", \"ProxyHopLimit\" FROM \"SecurityConfiguration\" WHERE \"Id\"=1 FOR SHARE")
            .UniqueResultAsync<object[]>(ct) ?? throw new ApiException(503, "Konfigurace zabezpečení chybí. Zkontrolujte migrace databáze.");
        return (Convert.ToInt64(row[0], System.Globalization.CultureInfo.InvariantCulture), Convert.ToInt32(row[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<IReadOnlyList<Guid>> LockGroupsForKeyShareAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken ct)
    {
        RequireTransaction();
        if (groupIds.Count == 0)
        {
            return [];
        }
        var ids = await m_session.CreateSQLQuery("SELECT \"Id\" FROM \"UserGroup\" WHERE \"Id\" IN (:ids) ORDER BY \"Id\" FOR KEY SHARE")
            .SetParameterList("ids", groupIds)
            .ListAsync<Guid>(ct);
        return ids.ToList();
    }

    public async Task<IReadOnlyList<Guid>> LockPagesForUpdateAsync(IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
    {
        RequireTransaction();
        // A space may hold more pages than a statement takes parameters.
        var ids = await m_session.CreateSQLQuery($"""
                SELECT "Id" FROM "Page" WHERE "Id" = {GuidList.Any(":ids")} ORDER BY "Id" FOR UPDATE
                """).SetParameter("ids", GuidList.Param(pageIds))
            .ListAsync<Guid>(ct);
        return ids.ToList();
    }

    /// <summary>
    /// The row FOR SHARE as it is under the lock, in one statement that locks and loads it. A load cannot overwrite
    /// an instance the session already holds, so such an instance is locked and refreshed instead.
    /// </summary>
    private async Task<T?> ReadSharedAsync<T>(string table, Guid id, CancellationToken ct) where T : class
    {
        RequireTransaction();
        if (m_session.Held<T>(id) is { } held)
        {
            if (!await LockRowAsync($"SELECT \"Id\" FROM \"{table}\" WHERE \"Id\"=:id FOR SHARE", id, ct))
            {
                return null;
            }
            await m_session.RefreshAsync(held, ct);
            return held;
        }
        return await m_session.CreateSQLQuery($"SELECT {{row.*}} FROM \"{table}\" {{row}} WHERE {{row}}.\"Id\"=:id FOR SHARE")
            .AddEntity("row", typeof(T))
            .SetParameter("id", id)
            .UniqueResultAsync<T?>(ct);
    }

    private void ForgetLocksOfEndedTransaction()
    {
        var transaction = m_session.GetCurrentTransaction();
        if (!ReferenceEquals(m_heldIn, transaction))
        {
            m_heldIn = transaction;
            m_contentShared = false;
            m_sharedSpaces.Clear();
        }
    }

    private async Task<bool> LockRowAsync(string sql, Guid id, CancellationToken ct)
    {
        RequireTransaction();
        var found = await m_session.CreateSQLQuery(sql)
            .SetParameter("id", id)
            .UniqueResultAsync(ct);
        return found is not null;
    }

    private void RequireTransaction()
    {
        if (m_session.GetCurrentTransaction() is not { IsActive: true })
        {
            throw new InvalidOperationException("Atlas locks require an active transaction.");
        }
    }
}
