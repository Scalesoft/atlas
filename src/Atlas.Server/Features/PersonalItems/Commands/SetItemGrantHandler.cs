using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.Notifications;
using Atlas.Server.Features.PersonalItems.Domain;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Objects;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalItems.Commands;

/// <summary>Shares one personal item with one user or group (Role 1 read, 2 write), changes the share or (Role null) ends it.</summary>
public sealed record SetItemGrant(Guid ObjectId, Guid? UserId, Guid? GroupId, int? Role, DateTimeOffset? StartAt, DateTimeOffset? ExpiresAt,
    string? ExpectedVersion);

/// <summary>
/// The item grant (spec 2026-10-29 §2, R1–R2), with the discipline of SetPageGrantHandler: the request checks first, the account and
/// the item's envelope FOR UPDATE (shares of one item serialize), only its owner shares (a grantee 403, anyone else 404), the subject
/// must exist and is not the owner, CAS on the current grant (its ActiveFrom), the no-op rule, a future end for new bounds, temporal
/// close + insert at Temporal.NextBoundary. ResourceGrant rows are immutable except for closing; TR_ResourceGrant_Audit audits them.
/// No fresh sign-in: the levels are read and write, as the page's lower grants. Live readers are revalidated after the commit.
/// </summary>
// Inbox (spec §6, inbox spec §3): item.shared (no current grant), item.share_changed and item.unshared (role null), to the user or the
// group's current members, in this transaction.
public sealed class SetItemGrantHandler(ISession session, IContentAccess access, PersonalItemTargets targets, AccessChanges accessChanges, Outbox outbox)
{
    public async Task Handle(SetItemGrant command, CancellationToken ct)
    {
        GrantValidity.RequireOneSubject(command.UserId, command.GroupId);
        if (command.Role is not (null or ObjectGrants.Read or ObjectGrants.Write))
        {
            throw new ApiException(400, "Vyberte úroveň sdílení: read (čte) nebo write (upravuje).");
        }
        var (startAt, expiresAt) = GrantValidity.Normalize(command.StartAt, command.ExpiresAt);
        var actor = await access.LockActorAsync(null, ct);
        var item = await targets.RequireOwnedAsync(actor, command.ObjectId, true, ct);
        await GrantSubjects.RequireExistsAsync(session, command.UserId, command.GroupId, ct);
        if (command.UserId == actor.Id)
        {
            throw new ApiException(400, "Položku vlastníte; sdílejte ji s někým jiným.");
        }
        var current = await Subject(item.Id, command.UserId, command.GroupId).GetExecutableQueryOver(session).And(x => x.ActiveTo == null).SingleOrDefaultAsync(ct);
        AdministrationVersion.Expect(current?.ActiveFrom, command.ExpectedVersion);
        if ((current is null && command.Role is null)
            || (current is not null && current.Role == command.Role && current.StartAt == startAt && current.ExpiresAt == expiresAt))
        {
            return;
        }
        if (command.Role is not null && (current is null || current.StartAt != startAt || current.ExpiresAt != expiresAt))
        {
            Rules.RequireFutureEnd(expiresAt, DateTime.UtcNow);
        }
        var last = current?.ActiveFrom ?? await Subject(item.Id, command.UserId, command.GroupId).GetExecutableQueryOver(session)
            .Select(Projections.Max<ResourceGrant>(x => x.ActiveTo)).SingleOrDefaultAsync<DateTime?>(ct);
        var boundary = last is null ? DateTime.UtcNow : Temporal.NextBoundary(last.Value, DateTime.UtcNow);
        if (current is not null)
        {
            var closed = await session.CreateSQLQuery("""UPDATE "ResourceGrant" SET "ActiveTo" = :boundary WHERE "Id" = :id AND "ActiveTo" IS NULL RETURNING "Id" """)
                .SetParameter("boundary", boundary, NHibernateUtil.UtcDateTime).SetParameter("id", current.Id).ListAsync<Guid>(ct);
            await AccessAuditLog.ClosedAsync(session, AccessAuditLog.ResourceGrant, [.. closed], actor.Id, ct);
        }
        if (command.Role is { } role)
        {
            var id = Guid.NewGuid();
            await session.CreateSQLQuery("""
                INSERT INTO "ResourceGrant"("Id", "ObjectId", "UserId", "GroupId", "Role", "StartAt", "ExpiresAt", "ActiveFrom")
                VALUES (:id, :object, :user, :group, :role, :start, :expires, :boundary)
                """)
                .SetParameter("id", id).SetParameter("object", item.Id)
                .SetParameter("user", command.UserId, NHibernateUtil.Guid).SetParameter("group", command.GroupId, NHibernateUtil.Guid)
                .SetParameter("role", role)
                .SetParameter("start", startAt, NHibernateUtil.UtcDateTime).SetParameter("expires", expiresAt, NHibernateUtil.UtcDateTime)
                .SetParameter("boundary", boundary, NHibernateUtil.UtcDateTime)
                .ExecuteUpdateAsync(ct);
            await AccessAuditLog.OpenedAsync(session, AccessAuditLog.ResourceGrant, [id], actor.Id, ct);
        }
        accessChanges.Changed();
        var kind = command.Role is null ? NotificationKinds.ItemUnshared : current is null ? NotificationKinds.ItemShared : NotificationKinds.ItemShareChanged;
        IList<Guid> recipients = command.UserId is { } user ? [user] : await session.CreateSQLQuery("""SELECT DISTINCT "UserId" FROM "EffectiveGroupMember" WHERE "GroupId" = :group""")
            .SetParameter("group", command.GroupId).ListAsync<Guid>(ct);
        outbox.Notify(new NotificationRequested(kind, item.Id, actor.Id, recipients.ToList()));
    }

    private static QueryOver<ResourceGrant, ResourceGrant> Subject(Guid objectId, Guid? userId, Guid? groupId)
    {
        var query = QueryOver.Of<ResourceGrant>().Where(x => x.ObjectId == objectId);
        return userId is { } user ? query.And(x => x.UserId == user) : query.And(x => x.GroupId == groupId);
    }
}
