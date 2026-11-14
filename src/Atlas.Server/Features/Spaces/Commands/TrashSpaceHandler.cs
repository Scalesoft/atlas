using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Spaces.Commands;

/// <summary>Moves a space to the trash (unified trash spec §3): only a wiki admin, who confirms by its name. Every live page goes
/// to the page trash in the batch of the space, so the restore brings exactly those back; pages trashed before keep their own.
/// Grants, vaults, templates, image and pins stay untouched: the space reads as missing (LiveSpace) until it is restored.</summary>
public sealed class TrashSpaceHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly CollaborationCheckpoints m_checkpoints;
    private readonly IRequestChannel m_channel;
    private readonly ContentParticipants m_participants;
    private readonly Outbox m_outbox;

    public TrashSpaceHandler(ISession session, IContentAccess access, IAtlasLocks locks, CollaborationCheckpoints checkpoints, IRequestChannel channel,
        ContentParticipants participants, Outbox outbox)
    {
        m_outbox = outbox;
        m_participants = participants;
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_checkpoints = checkpoints;
        m_channel = channel;
    }

    public async Task Handle(TrashSpace command, CancellationToken ct)
    {
        var actor = await LockAsWikiAdminAsync(m_locks, m_access, m_session, command.SpaceId, ct);
        var space = await LiveSpace.FindAsync(m_session, command.SpaceId, ct) ?? throw new ApiException(404, "Prostor nebyl nalezen.");
        if (!string.Equals(command.Request.Name?.Trim(), space.Name, StringComparison.Ordinal))
        {
            throw new ApiException(400, "Pro potvrzení opište přesný název prostoru.", "space_name_mismatch");
        }
        var spaceId = space.Id;
        // Ordered, so two writers never lock the same pages in opposite order.
        var ids = await m_session.QueryOver<Page>().Where(x => x.SpaceId == spaceId && !x.IsTrashed)
            .OrderBy(x => x.Id).Asc.Select(x => x.Id).ListAsync<Guid>(ct);
        var pages = await PageSubtree.LockSelectionAsync(m_session, m_access, m_locks, ids.ToList(), AccessOperation.EditContent, ct);
        var batch = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await PageTrash.MoveAsync(m_session, m_checkpoints, m_outbox, pages, actor.Id, batch, now, "Na některé stránce prostoru", m_channel.Current.RefusesOwnUnsaved, ct);
        // Boards and other nodes of the tree go along, those at the space root too (unified trash spec rule 4).
        foreach (var participant in m_participants.Tree)
        {
            await participant.TrashAsync(spaceId, ids.ToList(), true, new ContentTrashStamp(batch, now, actor.Id), ct);
        }
        space.TrashedAt = now;
        space.TrashedById = actor.Id;
        space.TrashBatchId = batch;
        await m_session.FlushAsync(ct);
    }

    /// <summary>The start of the space trash and its restore. Same lock order as the import: global membership, the space FOR
    /// UPDATE (a page created meanwhile waits for it and then finds the space gone), then the actor with the space, which page
    /// authorization needs. Whoever is no wiki admin gets 403 for every id, so the answer never tells which spaces exist.</summary>
    internal static async Task<UserAccount> LockAsWikiAdminAsync(IAtlasLocks locks, IContentAccess access, ISession session, Guid spaceId, CancellationToken ct)
    {
        await locks.AcquireContentSharedAsync(ct);
        var exists = await locks.LockSpaceForUpdateAsync(spaceId, ct);
        var actor = await access.LockActorAsync(exists ? spaceId : null, ct);
        await new SpaceRoles(session).RequireWikiAdminAsync(actor, ct, WikiAdminOnly);
        return exists ? actor : throw new ApiException(404, "Prostor nebyl nalezen.");
    }

    internal const string WikiAdminOnly = "Prostor do koše přesouvá a obnovuje správce wiki.";
}
