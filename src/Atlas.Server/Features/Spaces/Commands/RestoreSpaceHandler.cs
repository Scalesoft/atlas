using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Spaces.Commands;

/// <summary>Restores a trashed space and the pages trashed with it (its batch) to the places they had: every live page of a space
/// went with it, so each one's parent comes back too. Only a wiki admin.</summary>
public sealed class RestoreSpaceHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly ContentParticipants m_participants;
    private readonly Outbox m_outbox;

    public RestoreSpaceHandler(ISession session, IContentAccess access, IAtlasLocks locks, ContentParticipants participants, Outbox outbox)
    {
        m_outbox = outbox;
        m_participants = participants;
        m_session = session;
        m_access = access;
        m_locks = locks;
    }

    public async Task Handle(RestoreSpace command, CancellationToken ct)
    {
        await TrashSpaceHandler.LockAsWikiAdminAsync(m_locks, m_access, m_session, command.SpaceId, ct);
        var space = await m_session.GetAsync<Space>(command.SpaceId, ct);
        if (space is not { TrashBatchId: { } batch, PurgedAt: null })
        {
            throw new ApiException(404, "Prostor v koši nebyl nalezen.");
        }
        var spaceId = space.Id;
        var ids = await m_session.QueryOver<Page>().Where(x => x.SpaceId == spaceId && x.IsTrashed && x.TrashBatchId == batch && x.PurgedAt == null)
            .OrderBy(x => x.Id).Asc.Select(x => x.Id).ListAsync<Guid>(ct);
        PageTrash.Restore(m_outbox, await PageSubtree.LockSelectionAsync(m_session, m_access, m_locks, ids.ToList(), AccessOperation.ManageAccess, ct));
        foreach (var participant in m_participants.Tree)
        {
            await participant.RestoreAsync(spaceId, ids.ToList(), true, batch, ct);
        }
        space.TrashedAt = null;
        space.TrashedById = null;
        space.TrashBatchId = null;
        await m_session.FlushAsync(ct);
    }
}
