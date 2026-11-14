using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalLibrary.Domain;

/// <summary>Identity and page visibility for personal state. Writes of one account serialize on its personal advisory lock, so two browser tabs cannot race.</summary>
public sealed class PersonalPageGuard
{
    private readonly ISession m_session;
    private readonly ICurrentActor m_currentActor;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly PageGate m_gate;
    private readonly ContentParticipants m_participants;

    public PersonalPageGuard(ISession session, ICurrentActor currentActor, IContentAccess access, IAtlasLocks locks, PageGate gate,
        ContentParticipants participants)
    {
        m_session = session;
        m_currentActor = currentActor;
        m_access = access;
        m_locks = locks;
        m_gate = gate;
        m_participants = participants;
    }

    public async Task<UserAccount> LockIdentityAsync(Guid? spaceId, bool write, CancellationToken ct)
    {
        if (write)
        {
            await LockPersonalAsync(spaceId, ct);
        }
        return await m_access.LockActorAsync(spaceId, ct);
    }

    public async Task<UserAccount> RequirePageAsync(Guid pageId, bool write, CancellationToken ct)
    {
        return (await RequireResourceAsync(pageId, write, ct)).Actor;
    }

    /// <summary>A page as below, or another content type's resource (a board): the same personal locks, then that type's read
    /// decision (IContentLibraryParticipant). IsPage tells the page's own extras (page views) apart.</summary>
    public async Task<(UserAccount Actor, bool IsPage)> RequireResourceAsync(Guid pageId, bool write, CancellationToken ct)
    {
        // Lock order: the shared advisory comes first, before anything reads content.
        await m_locks.AcquireContentSharedAsync(ct);
        var spaceId = await m_session.QueryOver<Page>().Where(x => x.Id == pageId).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid>(ct);
        if (spaceId == Guid.Empty)
        {
            foreach (var participant in m_participants.Library)
            {
                if (await participant.SpaceOfAsync(pageId, ct) is { } resourceSpace)
                {
                    if (write)
                    {
                        await LockPersonalAsync(resourceSpace, ct);
                    }
                    return (await participant.RequireReadableAsync(pageId, ct), false);
                }
            }
            throw Missing();
        }
        if (write)
        {
            await LockPersonalAsync(spaceId, ct);
        }
        // The page gate from here: the space (already locked for a write) and the account, then the page under those locks.
        // ReadContent covers another owner's draft; a page that is missing, moved, in the trash or unreadable is the same 404.
        var space = await m_gate.EnterSpaceAsync(spaceId, ct);
        var page = (await space.ReadPageAsync(pageId, AccessOperation.ReadContent, ct)).Require();
        if (!HasPersonalState(page))
        {
            throw Missing();
        }
        return (page.Actor, true);
    }

    /// <summary>The owner's own draft is personal state only for an editor, as in 1.0.</summary>
    public static bool HasPersonalState(LockedPage page) => page.Page.PublishedRevisionId is not null || page.CanEdit;

    /// <summary>The locks of a personal write that come before the account, and so before the page gate: the space FOR SHARE,
    /// then the exclusive lock of the account's personal state.</summary>
    private async Task LockPersonalAsync(Guid? spaceId, CancellationToken ct)
    {
        // Take the exclusive per-account lock before LockActorAsync: upgrading its FOR SHARE would deadlock
        // two tabs of the same user. An advisory lock, not the account row FOR UPDATE, so personal writes
        // (a visit on every page open) no longer hold up the same user's parallel reads, which take the
        // row FOR SHARE. Account changes still exclude personal writes through that FOR SHARE.
        // The claimed id keys the lock; LockActorAsync then validates the account under its row lock.
        var claimed = m_currentActor.Claimed();
        await m_locks.AcquireContentSharedAsync(ct);
        if (spaceId is { } space)
        {
            await m_locks.LockSpaceSharedAsync(space, ct);
        }
        await m_locks.AcquireAdvisoryAsync($"personal:{claimed.Id:N}", ct);
    }

    public async Task<PersonalPageState> ReadStateAsync(Guid pageId, Guid userId, CancellationToken ct)
    {
        var favorite = await m_session.QueryOver<PageFavorite>()
            .Where(x => x.UserId == userId && x.PageId == pageId && x.ActiveTo == null)
            .Select(x => x.Id)
            .SingleOrDefaultAsync<Guid>(ct);
        var visited = await m_session.QueryOver<PageVisit>()
            .Where(x => x.UserId == userId && x.PageId == pageId)
            .Select(x => x.LastVisitedAt)
            .SingleOrDefaultAsync<DateTime?>(ct);
        return new PersonalPageState(favorite != Guid.Empty, visited);
    }

    public static ApiException Missing()
    {
        return new ApiException(404, "Stránka nebyla nalezena.");
    }
}
