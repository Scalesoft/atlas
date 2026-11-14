using NHibernate.Criterion;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Administration.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Commands;

public sealed class SetGroupMembershipHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;
    private readonly AccessChanges m_accessChanges;
    private readonly ISecurityChangeGuard m_guard;

    public SetGroupMembershipHandler(ISession session, AdministrationAccess access, AccessChanges accessChanges, ISecurityChangeGuard guard)
    {
        m_session = session;
        m_access = access;
        m_accessChanges = accessChanges;
        m_guard = guard;
    }

    public async Task Handle(SetGroupMembership command, CancellationToken ct)
    {
        var actor = await m_access.LockAdministrationAsync(ct);
        await AdministrationReads.GroupAsync(m_session, command.GroupId, ct);
        await LocalGroups.RequireAsync(m_session, command.GroupId, ct);
        await AdministrationReads.UserAsync(m_session, command.UserId, ct);
        var current = await m_session.QueryOver<GroupMember>()
            .Where(x => x.GroupId == command.GroupId && x.UserId == command.UserId && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct);
        AdministrationVersion.Expect(current?.ActiveFrom, command.Request.ExpectedVersion);
        if (command.Request.IsMember == (current is not null))
        {
            return;
        }
        // Membership conveys every grant assigned to the group, including protected administrative and secret access.
        await m_guard.RequireAsync(actor, new SecurityChange("group.membership", command.GroupId), ct);
        m_accessChanges.Changed();
        if (current is not null)
        {
            // The immutability guard accepts exactly this: ActiveTo moving away from NULL, every other value unchanged.
            current.ActiveTo = Temporal.NextBoundary(current.ActiveFrom, DateTime.UtcNow);
            await AccessAuditLog.ClosedAsync(m_session, AccessAuditLog.GroupMember, [current.Id], actor.Id, ct);
            return;
        }
        var previous = await m_session.QueryOver<GroupMember>()
            .Where(x => x.GroupId == command.GroupId && x.UserId == command.UserId)
            .Select(Projections.Max<GroupMember>(x => x.ActiveTo))
            .SingleOrDefaultAsync<DateTime?>(ct);
        var boundary = previous is null ? DateTime.UtcNow : Temporal.NextBoundary(previous.Value, DateTime.UtcNow);
        var member = new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = command.GroupId,
            UserId = command.UserId,
            ActiveFrom = boundary
        };
        await m_session.SaveAsync(member, ct);
        await AccessAuditLog.OpenedAsync(m_session, AccessAuditLog.GroupMember, [member.Id], actor.Id, ct);
    }
}
