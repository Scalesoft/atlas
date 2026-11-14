using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Commands;

public sealed class MovePageHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly OrganizationLocks m_locks;
    private readonly ContentParticipants m_participants;

    public MovePageHandler(ISession session, IContentAccess access, OrganizationLocks locks, ContentParticipants participants)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_participants = participants;
    }

    public async Task<OrganizationChange> Handle(MovePage command, CancellationToken ct)
    {
        var request = command.Request;
        var (page, actor) = await m_locks.LockPageAsync(command.PageId, false, AccessOperation.EditContent, ct);
        await TreeVersion.ExpectAsync(m_session, page.SpaceId, request.ExpectedTreeVersion, ct);
        await PageParent.ValidateAsync(m_session, m_access, page, request.ParentId, ct);
        var cancelled = await TitleOrder.HandMoveAsync(m_session, m_participants.Tree, actor, page.SpaceId, page.ParentId, request.ParentId, ct);
        var order = await PageSiblings.PositionAsync(m_session, m_participants.Tree, page.SpaceId, page.Id, request.ParentId, request.BeforePageId, actor, ct);
        // D2: every page keeps its own access mode and grants when it moves within the space, also under an own-access parent
        // (copy-on-create applies to new pages only; the impact preview of such a move is S6).
        page.ParentId = request.ParentId;
        page.SortOrder = order;
        await m_session.FlushAsync(ct);
        return new OrganizationChange(await TreeVersion.ReadAsync(m_session, page.SpaceId, ct), [page.Id], TitleOrderCancelled: cancelled);
    }
}
