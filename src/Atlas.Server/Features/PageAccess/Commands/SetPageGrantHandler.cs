using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.PageOrganization.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageAccess.Commands;

public sealed record SetPageGrant(Guid PageId, UpdatePageGrant Request);

/// <summary>
/// Adds, changes or removes the direct grant of one user or group on one page (WP3 S5), mirroring SetSpaceGrantHandler: the same
/// validation, CAS on the current assignment (its ActiveFrom), the no-op rule, temporal close + insert at Temporal.NextBoundary
/// (IX_ResourceGrant_*PreviousBoundary) and bounded delegation (ManageAccess on the page, never beyond the actor's own
/// authority). Page grants apply to exactly this page, with or without own access (D3). ResourceGrant rows are immutable
/// except for closing; the triggers write the audit events and close share links whose issuer loses Share. Granting or changing to
/// DocumentManager (manage) is a security change and needs a fresh sign-in (owner decision 3a); lower grants and revocations do not.
/// </summary>
public sealed class SetPageGrantHandler(ISession session, OrganizationLocks locks, ResourceGrantSnapshots grants, AccessChanges accessChanges,
    ISecurityChangeGuard guard)
{
    public async Task Handle(SetPageGrant command, CancellationToken ct)
    {
        var request = command.Request;
        var (role, startAt, expiresAt) = Validate(request);
        var (page, actor) = await locks.LockPageAsync(command.PageId, false, AccessOperation.ManageAccess, ct);
        await PageAccessAuthority.RequireAsync(grants, actor, page, role, expiresAt, ct);
        await GrantSubjects.RequireExistsAsync(session, request.UserId, request.GroupId, ct);
        var current = await CurrentAsync(session, page.Id, request.UserId, request.GroupId, ct);
        AdministrationVersion.Expect(current?.ActiveFrom, request.ExpectedVersion);
        // Revoking a missing grant is idempotent; a retained grant compares all business fields.
        if ((current is null && role is null) || (current is not null && current.Role == role && current.StartAt == startAt && current.ExpiresAt == expiresAt))
        {
            return;
        }
        // After the no-op: resending a manager grant that is already so changes nothing and needs no fresh sign-in (review A3-2).
        if (role == (int)AccessRole.DocumentManager)
        {
            await guard.RequireAsync(actor, new SecurityChange("page.grant", page.Id), ct);
        }
        // New or changed bounds must end in the future; a role change that resends lapsed bounds keeps them.
        if (role is not null && (current is null || current.StartAt != startAt || current.ExpiresAt != expiresAt))
        {
            Rules.RequireFutureEnd(expiresAt, DateTime.UtcNow);
        }
        // Subject and history reads may outlast a temporary manager's authority, including for revocation.
        await WriteAsync(session, page.Id, request.UserId, request.GroupId, current, role, startAt, expiresAt,
            () => PageAccessAuthority.RequireAsync(grants, actor, page, role, expiresAt, ct), actor.Id, ct);
        accessChanges.Changed();
    }

    private static async Task<ResourceGrant?> CurrentAsync(ISession session, Guid pageId, Guid? userId, Guid? groupId, CancellationToken ct)
        => await Subject(pageId, userId, groupId).GetExecutableQueryOver(session).And(x => x.ActiveTo == null).SingleOrDefaultAsync(ct);

    /// <summary>The one temporal write of a subject's page grant: the current one closes and the new one (role not null) starts at
    /// Temporal.NextBoundary after the subject's history. Who may write it is the caller's decision; <paramref name="beforeWrite"/>
    /// runs between the history read and the write; the audit names <paramref name="actorId"/>.</summary>
    internal static async Task WriteAsync(ISession session, Guid pageId, Guid? userId, Guid? groupId, ResourceGrant? current, int? role,
        DateTime? startAt, DateTime? expiresAt, Func<Task> beforeWrite, Guid actorId, CancellationToken ct)
    {
        var last = current?.ActiveFrom ?? await Subject(pageId, userId, groupId).GetExecutableQueryOver(session)
            .Select(Projections.Max<ResourceGrant>(x => x.ActiveTo))
            .SingleOrDefaultAsync<DateTime?>(ct);
        var boundary = last is null ? DateTime.UtcNow : Temporal.NextBoundary(last.Value, DateTime.UtcNow);
        await beforeWrite();
        if (current is not null)
        {
            var closed = await session.CreateSQLQuery("""
                UPDATE "ResourceGrant" SET "ActiveTo" = :boundary WHERE "Id" = :id AND "ActiveTo" IS NULL RETURNING "Id"
                """).SetParameter("boundary", boundary, NHibernateUtil.UtcDateTime).SetParameter("id", current.Id).ListAsync<Guid>(ct);
            await AccessAuditLog.ClosedAsync(session, AccessAuditLog.ResourceGrant, [.. closed], actorId, ct);
        }
        if (role is not null)
        {
            var id = Guid.NewGuid();
            await session.CreateSQLQuery("""
                INSERT INTO "ResourceGrant"("Id", "PageId", "UserId", "GroupId", "Role", "StartAt", "ExpiresAt", "ActiveFrom")
                VALUES (:id, :page, :user, :group, :role, :start, :expires, :boundary)
                """)
                .SetParameter("id", id).SetParameter("page", pageId)
                .SetParameter("user", userId, NHibernateUtil.Guid).SetParameter("group", groupId, NHibernateUtil.Guid)
                .SetParameter("role", role.Value)
                .SetParameter("start", startAt, NHibernateUtil.UtcDateTime).SetParameter("expires", expiresAt, NHibernateUtil.UtcDateTime)
                .SetParameter("boundary", boundary, NHibernateUtil.UtcDateTime)
                .ExecuteUpdateAsync(ct);
            await AccessAuditLog.OpenedAsync(session, AccessAuditLog.ResourceGrant, [id], actorId, ct);
        }
    }

    private static (int? Role, DateTime? StartAt, DateTime? ExpiresAt) Validate(UpdatePageGrant request)
    {
        GrantValidity.RequireOneSubject(request.UserId, request.GroupId);
        int? role = request.Role switch
        {
            null => null,
            "Reader" => 1,
            "Writer" => 2,
            "Admin" => 3,
            _ => throw new ApiException(400, "Neplatná role stránky.")
        };
        var (startAt, expiresAt) = GrantValidity.Normalize(request.StartAt, request.ExpiresAt);
        return (role, startAt, expiresAt);
    }

    private static QueryOver<ResourceGrant, ResourceGrant> Subject(Guid pageId, Guid? userId, Guid? groupId)
    {
        var query = QueryOver.Of<ResourceGrant>().Where(x => x.PageId == pageId);
        return userId is { } user
            ? query.And(x => x.UserId == user)
            : query.And(x => x.GroupId == groupId);
    }
}
