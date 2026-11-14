using NHibernate.Criterion;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Commands;

public sealed class SetSpaceGrantHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;
    private readonly SpaceRoles m_roles;
    private readonly AccessChanges m_accessChanges;
    private readonly ISecurityChangeGuard m_guard;

    public SetSpaceGrantHandler(ISession session, AdministrationAccess access, SpaceRoles roles, AccessChanges accessChanges,
        ISecurityChangeGuard guard)
    {
        m_session = session;
        m_access = access;
        m_roles = roles;
        m_accessChanges = accessChanges;
        m_guard = guard;
    }

    public async Task Handle(SetSpaceGrant command, CancellationToken ct)
    {
        var request = command.Request;
        var (role, startAt, expiresAt) = Validate(request);
        var actor = await m_access.RequireSpaceAdminAsync(command.SpaceId, true, ct);
        await RequireAuthorityAsync(actor, command.SpaceId, role, expiresAt, ct);
        if (request.UserId is { } userId)
        {
            await AdministrationReads.UserAsync(m_session, userId, ct);
        }
        else
        {
            await AdministrationReads.GroupAsync(m_session, request.GroupId!.Value, ct);
        }
        // Granting or changing to Admin (manage) is a security change (owner decision 3a); lower roles and revocations are not.
        if (role == (int)SpaceRole.Admin)
        {
            await m_guard.RequireAsync(actor, new SecurityChange("space.grant", command.SpaceId), ct);
        }
        var current = await Subject(command).GetExecutableQueryOver(m_session)
            .And(x => x.ActiveTo == null)
            .SingleOrDefaultAsync(ct);
        AdministrationVersion.Expect(current?.ActiveFrom, request.ExpectedVersion);
        // Revoking a missing grant is idempotent; retained assignments compare all business fields.
        if ((current is null && role is null) || (current is not null && current.Role == role && current.StartAt == startAt && current.ExpiresAt == expiresAt))
        {
            return;
        }
        // New or changed bounds must end in the future; a role change that resends lapsed bounds keeps them.
        if (role is not null && (current is null || current.StartAt != startAt || current.ExpiresAt != expiresAt))
        {
            Rules.RequireFutureEnd(expiresAt, DateTime.UtcNow);
        }
        await RequirePermanentAdminAsync(command.SpaceId, current, role, startAt, expiresAt, ct);
        await ReplaceAssignmentAsync(command, actor, current, role, startAt, expiresAt, ct);
        m_accessChanges.Changed();
    }

    private static (int? Role, DateTime? StartAt, DateTime? ExpiresAt) Validate(UpdateSpaceGrant request)
    {
        GrantValidity.RequireOneSubject(request.UserId, request.GroupId);
        int? role = request.Role switch
        {
            null => null,
            "Reader" => 1,
            "Writer" => 2,
            "Admin" => 3,
            _ => throw new ApiException(400, "Neplatná role prostoru.")
        };
        var (startAt, expiresAt) = GrantValidity.Normalize(request.StartAt, request.ExpiresAt);
        return (role, startAt, expiresAt);
    }

    private async Task RequirePermanentAdminAsync(Guid spaceId, SpaceGrant? current, int? role, DateTime? startAt, DateTime? expiresAt, CancellationToken ct)
    {
        var now = await m_session.CreateSQLQuery("SELECT statement_timestamp()").UniqueResultAsync<DateTime>(ct);
        var wasPermanentAdmin = current is { Role: 3, ExpiresAt: null } && (current.StartAt is null || current.StartAt <= now);
        var staysPermanentAdmin = role == 3 && expiresAt is null && (startAt is null || startAt <= now);
        if (wasPermanentAdmin && !staysPermanentAdmin && await PermanentAdmins(m_session).And(x => x.SpaceId == spaceId).RowCountAsync(ct) <= 1)
        {
            throw new ApiException(409, "Prostor musí mít alespoň jedno explicitní přiřazení správce.");
        }
    }

    /// <summary>The explicit Admin assignments a space must keep one of: current, started and without an expiry (also revoke-all).</summary>
    internal static NHibernate.IQueryOver<SpaceGrant, SpaceGrant> PermanentAdmins(ISession session)
    {
        return session.QueryOver<SpaceGrant>()
            .Where(x => x.Role == 3 && x.ActiveTo == null)
            .And(x => x.ExpiresAt == null)
            .And(Expression.Sql("({alias}.\"StartAt\" IS NULL OR {alias}.\"StartAt\" <= statement_timestamp())"));
    }

    private async Task ReplaceAssignmentAsync(SetSpaceGrant command, UserAccount actor, SpaceGrant? current, int? role, DateTime? startAt, DateTime? expiresAt, CancellationToken ct)
    {
        var request = command.Request;
        var last = current?.ActiveFrom ?? await Subject(command).GetExecutableQueryOver(m_session)
            .Select(Projections.Max<SpaceGrant>(x => x.ActiveTo))
            .SingleOrDefaultAsync<DateTime?>(ct);
        var boundary = last is null ? DateTime.UtcNow : Temporal.NextBoundary(last.Value, DateTime.UtcNow);
        // Subject/history reads may outlast a temporary administrator's authority, including for revocation.
        await RequireAuthorityAsync(actor, command.SpaceId, role, expiresAt, ct);
        if (current is not null)
        {
            current.ActiveTo = boundary;
            // Flushes first: NHibernate flushes inserts before updates, and the partial unique index would see two current grants.
            await AccessAuditLog.ClosedAsync(m_session, AccessAuditLog.SpaceGrant, [current.Id], actor.Id, ct);
        }
        if (role is not null)
        {
            var grant = new SpaceGrant
            {
                Id = Guid.NewGuid(),
                SpaceId = command.SpaceId,
                UserId = request.UserId,
                GroupId = request.GroupId,
                Role = role.Value,
                StartAt = startAt,
                ExpiresAt = expiresAt,
                ActiveFrom = boundary
            };
            await m_session.SaveAsync(grant, ct);
            await AccessAuditLog.OpenedAsync(m_session, AccessAuditLog.SpaceGrant, [grant.Id], actor.Id, ct);
        }
    }

    private async Task RequireAuthorityAsync(UserAccount actor, Guid spaceId, int? role, DateTime? expiresAt, CancellationToken ct)
    {
        var authority = await m_roles.AdminAuthorityAsync(actor, spaceId, ct);
        if (!authority.Allowed || (role is not null && authority.ExpiresAt is { } authorityEnd && (expiresAt is null || expiresAt > authorityEnd)))
            throw new ApiException(403, "Přístup nelze udělit na dobu delší, než je platnost vašeho oprávnění správce.");
    }

    private static QueryOver<SpaceGrant, SpaceGrant> Subject(SetSpaceGrant command)
    {
        var request = command.Request;
        var query = QueryOver.Of<SpaceGrant>().Where(x => x.SpaceId == command.SpaceId);
        return request.UserId is { } userId
            ? query.And(x => x.UserId == userId)
            : query.And(x => x.GroupId == request.GroupId);
    }
}
