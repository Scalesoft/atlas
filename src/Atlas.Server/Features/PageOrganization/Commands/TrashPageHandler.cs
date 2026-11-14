using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Commands;

public sealed class TrashPageHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly OrganizationLocks m_locks;
    private readonly IAtlasLocks m_atlasLocks;
    private readonly CollaborationCheckpoints m_checkpoints;
    private readonly IRequestChannel m_channel;
    private readonly ContentParticipants m_participants;
    private readonly Outbox m_outbox;

    public TrashPageHandler(ISession session, IContentAccess access, OrganizationLocks locks, IAtlasLocks atlasLocks, CollaborationCheckpoints checkpoints, IRequestChannel channel,
        ContentParticipants participants, Outbox outbox)
    {
        m_outbox = outbox;
        m_channel = channel;
        m_participants = participants;
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_atlasLocks = atlasLocks;
        m_checkpoints = checkpoints;
    }

    public async Task<OrganizationChange> Handle(TrashPage command, CancellationToken ct)
    {
        var request = command.Request;
        var (page, actor) = await m_locks.LockPageAsync(command.PageId, false, AccessOperation.EditContent, ct);
        await TreeVersion.ExpectAsync(m_session, page.SpaceId, request.ExpectedTreeVersion, ct);
        Rules.ExpectVersion(page.Version, request.ExpectedVersion);
        var ids = await PageSubtree.DescendantsAsync(m_session, page, false, ct);
        if (!request.IncludeSubtree && ids.Count > 1)
        {
            throw new ApiException(409, "Stránka obsahuje podstránky. Potvrďte přesun celého podstromu do koše.", "subtree_confirmation_required");
        }
        var pages = await PageSubtree.LockSelectionAsync(m_session, m_access, m_atlasLocks, ids, AccessOperation.EditContent, ct);
        var where = pages.Count <= 1 ? "Na stránce" : "Na stránce nebo některé z jejích podstránek";
        var batch = Guid.NewGuid();
        var now = DateTime.UtcNow;
        // The actor's own unsaved edits are refused too over mcp and assistant (D3, S5); the Page locks exclude every append writer.
        await PageTrash.MoveAsync(m_session, m_checkpoints, m_outbox, pages, actor.Id, batch, now, where, m_channel.Current.RefusesOwnUnsaved, ct);
        // The other content types' leaves under these pages join the batch, so restoring it brings them back (interaction spec 6).
        foreach (var participant in m_participants.Tree)
        {
            await participant.TrashAsync(page.SpaceId, ids, false, new ContentTrashStamp(batch, now, actor.Id), ct);
        }
        await m_session.FlushAsync(ct);
        return new OrganizationChange(await TreeVersion.ReadAsync(m_session, page.SpaceId, ct), ids);
    }
}
