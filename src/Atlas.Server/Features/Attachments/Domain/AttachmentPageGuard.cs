using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Attachments.Domain;

/// <summary>How an attachment operation locks its page: reads share it with other reads, stores exclude writers.</summary>
public enum AttachmentPageLock
{
    None,
    Shared,
    Update
}

/// <summary>Resolves the page behind an attachment operation under the standard content lock order and hides pages the actor may not see.</summary>
public sealed class AttachmentPageGuard
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;

    public AttachmentPageGuard(ISession session, IContentAccess access, IAtlasLocks locks)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
    }

    /// <summary>Opening needs ReadContent, uploads EditContent. A draft's attachments need EditContent and ownership or WikiAdmin.</summary>
    public async Task<(Page Page, UserAccount Actor, bool CanEdit)> RequirePageAsync(Guid pageId, AccessOperation operation, AttachmentPageLock lockMode, CancellationToken ct)
    {
        var locking = lockMode != AttachmentPageLock.None;
        // Legacy lock order: the shared advisory comes first, before anything reads content, also for the unlocked pre-check.
        // LockActorAsync takes it again below, which is a no-op once it is held.
        await m_locks.AcquireContentSharedAsync(ct);
        var page = await m_session.GetAsync<Page>(pageId, ct) ?? throw Missing();
        var originalSpace = page.SpaceId;
        var actor = await m_access.LockActorAsync(page.SpaceId, ct);
        if (locking)
        {
            // A read only has to exclude writers; FOR UPDATE here served the images of one page one at a time.
            var locked = lockMode == AttachmentPageLock.Update
                ? await m_locks.LockPageForUpdateAsync(page.Id, ct)
                : await m_locks.LockPageSharedAsync(page.Id, ct);
            if (!locked)
            {
                throw Missing();
            }
            await m_session.RefreshAsync(page, ct);
            if (page.SpaceId != originalSpace)
            {
                throw new ApiException(409, "Stránka byla přesunuta. Načtěte ji znovu.");
            }
        }
        else
        {
            // Unlocked pre-check: still decide on the row as it is after LockActorAsync, as the RequirePageAsync contract asks.
            await m_session.RefreshAsync(page, ct);
        }
        if (page.IsTrashed)
        {
            throw Missing();
        }
        var authorization = await m_access.AuthorizePageAsync(page, operation, ct);
        if (authorization.Outcome == PageAccessOutcome.Missing)
        {
            throw Missing();
        }
        // A reader of the page is told why an upload is refused (403).
        authorization.Require();
        if (page.PublishedRevisionId is null && !authorization.CanEdit)
        {
            throw Missing();
        }
        return (page, actor, authorization.CanEdit);
    }

    public static ApiException Missing()
    {
        return new ApiException(404, "Příloha není dostupná.");
    }
}
