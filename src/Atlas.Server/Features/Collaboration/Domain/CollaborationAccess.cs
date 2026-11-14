using NHibernate;
using NHibernate.Criterion;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Domain;

/// <summary>Write boundary of the relay: the same lock order as page writes, but authorizing the account the hub connected with.</summary>
public sealed class CollaborationAccess
{
    private readonly ISession m_session;
    private readonly IAtlasLocks m_locks;
    private readonly ResourceGrantSnapshots m_grants;

    public CollaborationAccess(ISession session, IAtlasLocks locks, ResourceGrantSnapshots grants)
    {
        m_session = session;
        m_locks = locks;
        m_grants = grants;
    }

    public async Task<Page> RequireWriterAsync(Guid pageId, Guid actorId, string? expectedStamp, CancellationToken ct)
    {
        await m_locks.AcquireContentSharedAsync(ct);
        var spaceId = await m_session.QueryOver<Page>()
            .Where(x => x.Id == pageId)
            .Select(x => x.SpaceId)
            .SingleOrDefaultAsync<Guid>(ct);
        if (spaceId == Guid.Empty)
        {
            throw new ApiException(404, "Stránka není dostupná.");
        }
        await m_locks.LockSpaceSharedAsync(spaceId, ct);
        // Read under the account lock, in one statement; an instance already in the first-level cache (ICurrentActor.RequireAsync)
        // is refreshed there, so a stamp rotated behind this session's back is noticed.
        var actor = await m_locks.ReadAccountSharedAsync(actorId, ct);
        if (actor is null || !actor.IsActive || (expectedStamp is not null && actor.SecurityStamp != expectedStamp))
        {
            throw new ApiException(401, "Přihlášení vypršelo.");
        }
        var page = await m_session.GetForUpdateAsync<Page>(pageId, ct);
        if (page is null || page.IsTrashed || page.SpaceId != spaceId)
        {
            throw Unavailable();
        }
        // EditContent on the page, evaluated after the page lock: waiting for it can outlast a grant even while the content
        // locks are held. The relay authorizes the hub's account, not the request actor, so it cannot use IContentAccess.
        if (!(await m_grants.AuthorizePageAsync(actor, page, AccessOperation.EditContent, ct)).Allowed)
        {
            throw Unavailable();
        }
        return page;
    }

    private static ApiException Unavailable()
    {
        return new ApiException(404, "Stránka není dostupná.");
    }

    public async Task<CollaborationHead> LockHeadAsync(Guid pageId, Guid generation, CancellationToken ct)
    {
        if (await m_session.GetForUpdateAsync<Page>(pageId, ct) is not { IsTrashed: false })
        {
            throw CollaborationErrors.Stale();
        }
        // Per-update reads deliberately exclude the potentially multi-megabyte snapshot and projections.
        var row = await m_session.QueryOver<PageCollaboration>()
            .Where(x => x.PageId == pageId)
            .Select(Projections.Property<PageCollaboration>(x => x.Generation), Projections.Property<PageCollaboration>(x => x.Sequence),
                Projections.Property<PageCollaboration>(x => x.SnapshotSequence), Projections.Property<PageCollaboration>(x => x.PendingCount),
                Projections.Property<PageCollaboration>(x => x.PendingBytes), Projections.Property<PageCollaboration>(x => x.LastSavedSequence),
                Projections.SqlProjection("""
                    ({alias}."BaseRevisionId" IS DISTINCT FROM (SELECT r."Id" FROM "PageRevision" r WHERE r."PageId" = {alias}."PageId" AND r."ActiveTo" IS NULL)) AS "Behind"
                    """, ["Behind"], [NHibernateUtil.Boolean]))
            .SingleOrDefaultAsync<object[]>(ct);
        if (row is null || (Guid)row[0] != generation)
        {
            throw CollaborationErrors.Stale();
        }
        return new CollaborationHead(pageId, generation, (long)row[1], (long)row[2], (int)row[3], (long)row[4], (long?)row[5] ?? 0, (bool)row[6]);
    }
}
