using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Domain;

/// <summary>The lock order every organization operation shares: advisory shared, space, actor, then the page row.</summary>
public sealed class OrganizationLocks
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;

    public OrganizationLocks(ISession session, IContentAccess access, IAtlasLocks locks)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
    }

    public async Task<UserAccount> LockForReadAsync(Guid spaceId, CancellationToken ct)
    {
        await m_locks.AcquireContentSharedAsync(ct);
        // A missing space reports the page message, not the space message; check before LockActorAsync throws its own.
        if (!await LiveSpace.ExistsAsync(m_session, spaceId, ct))
        {
            throw Missing();
        }
        return await m_access.LockActorAsync(spaceId, ct);
    }

    public async Task<UserAccount> LockForWriteAsync(Guid spaceId, CancellationToken ct)
    {
        await m_locks.AcquireContentSharedAsync(ct);
        if (!await m_locks.LockSpaceForUpdateAsync(spaceId, ct))
        {
            throw Missing();
        }
        // The space is already held FOR UPDATE; passing it adds a compatible FOR SHARE and lets RequirePageAsync see it locked.
        return await m_access.LockActorAsync(spaceId, ct);
    }

    public async Task<(Page Page, UserAccount Actor)> LockPageAsync(Guid pageId, bool trashed, AccessOperation operation, CancellationToken ct)
    {
        // Lock order: the shared advisory comes first, before anything reads content.
        // LockForWriteAsync takes it again below, which is a no-op once it is held.
        await m_locks.AcquireContentSharedAsync(ct);
        var spaceId = await m_session.QueryOver<Page>().Where(x => x.Id == pageId).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid>(ct);
        if (spaceId == Guid.Empty)
        {
            throw Missing();
        }
        var actor = await LockForWriteAsync(spaceId, ct);
        // Shared handlers (rename, grants, access mode) run inside other handlers' transactions: a page one of them already loaded
        // is taken as it is under the lock, not as it was loaded.
        var page = await m_session.GetForUpdateAsync<Page>(pageId, ct) ?? throw Missing();
        if (page.SpaceId != spaceId)
        {
            throw new ApiException(409, "Stránka byla přesunuta. Načtěte ji znovu.", "space_changed");
        }
        // A purged page is a tombstone of the trash: missing to every organization operation.
        if (page.IsTrashed != trashed || page.PurgedAt is not null)
        {
            throw Missing();
        }
        await m_access.RequirePageAsync(page, operation, ct);
        return (page, actor);
    }

    /// <summary>
    /// The space trash is the space's administration: its Admin role (or WikiAdmin), after the page decision under the locks. A page's
    /// own manage grant gives ManageAccess on the page but no trash, which it does not even list (SEC3-2, SEC-Z-1). The account knows
    /// the page, so the refusal is 403 with <paramref name="refusal"/>.
    /// </summary>
    public static async Task RequireSpaceAdministrationAsync(IContentAccess access, Guid spaceId, string refusal, CancellationToken ct)
    {
        try
        {
            await access.RequireSpaceAsync(spaceId, SpaceRole.Admin, ct);
        }
        catch (ApiException ex) when (ex.Status is 403 or 404)
        {
            throw new ApiException(403, refusal);
        }
    }

    public static ApiException Missing()
    {
        return new ApiException(404, "Stránka nebyla nalezena.");
    }
}
