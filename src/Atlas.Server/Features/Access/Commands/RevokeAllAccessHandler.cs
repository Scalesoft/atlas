using NHibernate.Criterion;
using Atlas.Server.Features.Administration;
using Atlas.Server.Features.Administration.Commands;
using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.Notifications;
using Atlas.Server.Features.PersonalApi.Domain;
using Atlas.Server.Platform.Events;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Access.Commands;

/// <summary>
/// "Odebrat vše" (WikiAdmin, fresh sign-in): closes the account's own current space, page, vault and secret grants and its group
/// local memberships and revokes its personal API tokens that could still authenticate (RevokeAllRows counts the same rows). Group grants
/// stay with their groups, and the account stays active; deactivating it is a separate action. The whole administration surface is
/// serialized (content lock exclusive, as account administration), so no content decision runs between the closures. A space for
/// which the account holds the last permanent Admin assignment is refused with 409, the rule of SetSpaceGrantHandler. Each personal
/// item whose share it closes tells the account item.unshared (inbox spec §3), from the administrator.
/// </summary>
public sealed class RevokeAllAccessHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_administration;
    private readonly IAtlasLocks m_locks;
    private readonly ISecurityChangeGuard m_guard;
    private readonly AccessChanges m_accessChanges;
    private readonly Outbox m_outbox;

    public RevokeAllAccessHandler(ISession session, AdministrationAccess administration, IAtlasLocks locks, ISecurityChangeGuard guard,
        AccessChanges accessChanges, Outbox outbox)
    {
        m_session = session;
        m_administration = administration;
        m_locks = locks;
        m_guard = guard;
        m_accessChanges = accessChanges;
        m_outbox = outbox;
    }

    public async Task Handle(RevokeAllAccess command, CancellationToken ct)
    {
        var actor = await m_administration.LockAdministrationAsync(ct);
        if (!await m_locks.LockAccountForUpdateAsync(command.UserId, ct))
        {
            throw new ApiException(404, "Uživatel nebyl nalezen.");
        }
        await RequireOtherAdminsAsync(command.UserId, ct);
        await m_guard.RequireAsync(actor, new SecurityChange("account.revoke_all", command.UserId), ct);
        // Category 4: one set write per table closes every current row, as SetPageAccessModeHandler and PageItemAccess close grants;
        // each closure is audited with the locked actor and closes the share links whose issuer loses Share.
        var grants = await m_session.CreateSQLQuery("""
            UPDATE "SpaceGrant" SET "ActiveTo" = greatest(clock_timestamp(), "ActiveFrom" + interval '1 microsecond')
            WHERE "UserId" = :user AND "ActiveTo" IS NULL
            RETURNING "Id"
            """).SetParameter("user", command.UserId).ListAsync<Guid>(ct);
        await AccessAuditLog.ClosedAsync(m_session, AccessAuditLog.SpaceGrant, [.. grants], actor.Id, ct);
        var items = await m_session.CreateSQLQuery("""
            UPDATE "ResourceGrant" SET "ActiveTo" = greatest(clock_timestamp(), "ActiveFrom" + interval '1 microsecond')
            WHERE "UserId" = :user AND "ActiveTo" IS NULL
            RETURNING "Id", "ObjectId"
            """).SetParameter("user", command.UserId).ListAsync<object[]>(ct);
        await AccessAuditLog.ClosedAsync(m_session, AccessAuditLog.ResourceGrant, [.. items.Select(x => (Guid)x[0])], actor.Id, ct);
        foreach (var item in items.Select(x => x[1]).OfType<Guid>().Distinct())
        {
            m_outbox.Notify(new NotificationRequested(NotificationKinds.ItemUnshared, item, actor.Id, [command.UserId]));
        }
        var memberships = await RevokeAllRows.CloseLocalMembershipsAsync(m_session, command.UserId, ct);
        await AccessAuditLog.ClosedAsync(m_session, AccessAuditLog.GroupMember, memberships, actor.Id, ct);
        var now = DateTime.UtcNow;
        var tokens = await m_session.QueryOver<PersonalApiToken>()
            .Where(t => t.UserId == command.UserId && t.ActiveTo == null && t.ExpiresAt > now)
            .ListAsync(ct);
        foreach (var token in tokens)
        {
            token.ActiveTo = Temporal.NextBoundary(token.ActiveFrom, now);
        }
        m_accessChanges.Changed();
    }

    private async Task RequireOtherAdminsAsync(Guid userId, CancellationToken ct)
    {
        var own = await SetSpaceGrantHandler.PermanentAdmins(m_session).And(g => g.UserId == userId).Select(g => g.SpaceId).ListAsync<Guid>(ct);
        var blocked = new List<Guid>();
        foreach (var spaceId in own.Distinct())
        {
            if (await SetSpaceGrantHandler.PermanentAdmins(m_session).And(g => g.SpaceId == spaceId).RowCountAsync(ct) <= 1)
            {
                blocked.Add(spaceId);
            }
        }
        if (blocked.Count == 0)
        {
            return;
        }
        var names = await m_session.QueryOver<Space>()
            .Where(GrantQueryLimits.InChunks(Projections.Property<Space>(s => s.Id), blocked))
            .Select(s => s.Name).ListAsync<string>(ct);
        throw new ApiException(409,
            $"Prostor musí mít alespoň jedno explicitní přiřazení správce. Nejdřív přidejte jiného správce: {string.Join(", ", names.Order(StringComparer.CurrentCulture))}.");
    }
}
