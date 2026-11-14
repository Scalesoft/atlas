using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Commands;

/// <summary>
/// Pins a level of the tree to its titles or unpins it (<see cref="TitleOrder"/>; unpinned, it keeps the order it showed). It changes
/// the order of the level's children, so it needs what placing a child there needs (PageParent): Writer on the space for the root,
/// EditContent on the parent page otherwise. The same value again changes nothing. Undo is the same call with the other value.
/// </summary>
public sealed class SortTreeLevelHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly OrganizationLocks m_locks;
    private readonly ContentParticipants m_participants;

    public SortTreeLevelHandler(ISession session, IContentAccess access, OrganizationLocks locks, ContentParticipants participants)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_participants = participants;
    }

    public async Task<OrganizationChange> Handle(SortTreeLevel command, CancellationToken ct)
    {
        var parentId = command.Request.ParentId;
        UserAccount actor;
        if (parentId is { } id)
        {
            (var page, actor) = await m_locks.LockPageAsync(id, false, AccessOperation.EditContent, ct);
            if (page.SpaceId != command.SpaceId || page.IsTemplate)
            {
                throw OrganizationLocks.Missing();
            }
        }
        else
        {
            actor = await m_locks.LockForWriteAsync(command.SpaceId, ct);
            await m_access.RequireSpaceAsync(command.SpaceId, SpaceRole.Writer, ct);
        }
        await TitleOrder.SetAsync(m_session, m_participants.Tree, actor, command.SpaceId, parentId, command.Request.ByTitle, ct);
        return new OrganizationChange(await TreeVersion.ReadAsync(m_session, command.SpaceId, ct), parentId is { } parent ? [parent] : []);
    }
}
