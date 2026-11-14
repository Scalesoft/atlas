using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.Vaults.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Commands;

/// <summary>
/// Adds, changes and revokes vault and secret grants (WP4 V3), mirroring SetPageGrantHandler: validation, manage on the
/// target (404 when the actor cannot even Discover it, 403 otherwise), bounded delegation (a grant never outlasts the actor's own
/// manage), the security change guard (fresh re-authentication from V4), CAS on the current assignment (its ActiveFrom),
/// the no-op rule, and temporal close + insert at Temporal.NextBoundary (IX_ResourceGrant_VaultHistory). The vault's space is held
/// FOR UPDATE (VaultAccess). ResourceGrant rows are immutable except for closing; TR_ResourceGrant_Audit writes the audit events
/// with the locked actor, and AccessChanges refreshes live sessions after the commit.
/// </summary>
public sealed class VaultGrantHandler
{
    private readonly ISession m_session;
    private readonly VaultAccess m_access;
    private readonly ISecurityChangeGuard m_guard;
    private readonly AccessChanges m_accessChanges;

    public VaultGrantHandler(ISession session, VaultAccess access, ISecurityChangeGuard guard, AccessChanges accessChanges)
    {
        m_session = session;
        m_access = access;
        m_guard = guard;
        m_accessChanges = accessChanges;
    }

    public async Task Handle(SetVaultGrant command, CancellationToken ct)
    {
        var (level, startAt, expiresAt) = Validate(command.Request);
        await ApplyAsync(await m_access.ReadVaultAsync(command.VaultId, true, ct), command.Request.UserId, command.Request.GroupId,
            level, startAt, expiresAt, command.Request.ExpectedVersion, ct);
    }

    public async Task Handle(RevokeVaultGrant command, CancellationToken ct)
    {
        GrantValidity.RequireOneSubject(command.UserId, command.GroupId);
        await ApplyAsync(await m_access.ReadVaultAsync(command.VaultId, true, ct), command.UserId, command.GroupId, null, null, null,
            command.ExpectedVersion, ct);
    }

    public async Task Handle(SetSecretGrant command, CancellationToken ct)
    {
        var (level, startAt, expiresAt) = Validate(command.Request);
        await ApplyAsync(await m_access.ReadSecretAsync(command.SecretId, true, ct), command.Request.UserId, command.Request.GroupId,
            level, startAt, expiresAt, command.Request.ExpectedVersion, ct);
    }

    public async Task Handle(RevokeSecretGrant command, CancellationToken ct)
    {
        GrantValidity.RequireOneSubject(command.UserId, command.GroupId);
        await ApplyAsync(await m_access.ReadSecretAsync(command.SecretId, true, ct), command.UserId, command.GroupId, null, null, null,
            command.ExpectedVersion, ct);
    }

    /// <summary>Level null revokes. Returns the current grant after the change (null after a revocation); also the approval path of
    /// access requests (WP4 V5), so an approved request is an ordinary grant with this handler's rules.</summary>
    internal async Task<Guid?> ApplyAsync(VaultAccessTarget target, Guid? userId, Guid? groupId, VaultAccessLevel? level, DateTime? startAt,
        DateTime? expiresAt, string? expectedVersion, CancellationToken ct)
    {
        RequireAuthority(target, level, expiresAt);
        await GrantSubjects.RequireExistsAsync(m_session, userId, groupId, ct);
        var vault = target.Vault!;
        var secretId = target.Secret?.Id;
        await m_guard.RequireAsync(target.Actor, new SecurityChange(secretId is null ? "vault.grant" : "secret.grant", secretId ?? vault.Id), ct);
        var current = await Subject(vault.Id, secretId, userId, groupId).GetExecutableQueryOver(m_session)
            .And(x => x.ActiveTo == null)
            .SingleOrDefaultAsync(ct);
        AdministrationVersion.Expect(current?.ActiveFrom, expectedVersion);
        var role = (int?)level;
        // Revoking a missing grant is idempotent; a retained grant compares all business fields.
        if ((current is null && role is null) || (current is not null && current.Role == role && current.StartAt == startAt && current.ExpiresAt == expiresAt))
        {
            return current?.Id;
        }
        // New or changed bounds must end in the future; a level change that resends lapsed bounds keeps them.
        if (role is not null && (current is null || current.StartAt != startAt || current.ExpiresAt != expiresAt))
        {
            Rules.RequireFutureEnd(expiresAt, DateTime.UtcNow);
        }
        var last = current?.ActiveFrom ?? await Subject(vault.Id, secretId, userId, groupId).GetExecutableQueryOver(m_session)
            .Select(Projections.Max<ResourceGrant>(x => x.ActiveTo))
            .SingleOrDefaultAsync<DateTime?>(ct);
        var boundary = last is null ? DateTime.UtcNow : Temporal.NextBoundary(last.Value, DateTime.UtcNow);
        // Subject and history reads may outlast a temporary manager's authority, including for revocation.
        RequireAuthority(target, level, expiresAt);
        if (current is not null)
        {
            var closed = await m_session.CreateSQLQuery("""
                UPDATE "ResourceGrant" SET "ActiveTo" = :boundary WHERE "Id" = :id AND "ActiveTo" IS NULL RETURNING "Id"
                """).SetParameter("boundary", boundary, NHibernateUtil.UtcDateTime).SetParameter("id", current.Id).ListAsync<Guid>(ct);
            await AccessAuditLog.ClosedAsync(m_session, AccessAuditLog.ResourceGrant, [.. closed], target.Actor.Id, ct);
        }
        Guid? grantId = null;
        if (role is not null)
        {
            grantId = Guid.NewGuid();
            await m_session.CreateSQLQuery("""
                INSERT INTO "ResourceGrant"("Id", "VaultId", "SecretId", "VaultSpaceId", "UserId", "GroupId", "Role", "StartAt", "ExpiresAt", "ActiveFrom")
                VALUES (:id, :vault, :secret, :space, :user, :group, :role, :start, :expires, :boundary)
                """)
                .SetParameter("id", grantId.Value).SetParameter("vault", vault.Id).SetParameter("secret", secretId, NHibernateUtil.Guid)
                .SetParameter("space", vault.SpaceId)
                .SetParameter("user", userId, NHibernateUtil.Guid).SetParameter("group", groupId, NHibernateUtil.Guid)
                .SetParameter("role", role.Value)
                .SetParameter("start", startAt, NHibernateUtil.UtcDateTime).SetParameter("expires", expiresAt, NHibernateUtil.UtcDateTime)
                .SetParameter("boundary", boundary, NHibernateUtil.UtcDateTime)
                .ExecuteUpdateAsync(ct);
            await AccessAuditLog.OpenedAsync(m_session, AccessAuditLog.ResourceGrant, [grantId.Value], target.Actor.Id, ct);
        }
        m_accessChanges.Changed();
        return grantId;
    }

    /// <summary>Manage on the target now (bounded delegation, as PageAccessAuthority): a grant never exceeds the actor's own level
    /// and never outlasts the actor's own authority (the space Admin grant or the vault or secret grants that give it manage).</summary>
    private static void RequireAuthority(VaultAccessTarget target, VaultAccessLevel? level, DateTime? expiresAt)
    {
        var authority = target.Authorize(VaultAccessLevel.Manage).Require();
        if (level is { } granted && (granted > authority.Level || (authority.Until is { } end && (expiresAt is null || expiresAt > end))))
        {
            throw new ApiException(403, "Přístup nelze udělit vyšší ani na dobu delší, než je vaše vlastní oprávnění spravovat trezor nebo heslo.");
        }
    }

    private static (VaultAccessLevel Level, DateTime? StartAt, DateTime? ExpiresAt) Validate(UpdateVaultGrant request)
    {
        GrantValidity.RequireOneSubject(request.UserId, request.GroupId);
        var level = VaultAccessLevels.Parse(request.Level)
            ?? throw new ApiException(400, "Vyberte úroveň přístupu: discover (vidí), read (čte) nebo manage (spravuje).");
        var (startAt, expiresAt) = GrantValidity.Normalize(request.StartAt, request.ExpiresAt);
        return (level, startAt, expiresAt);
    }

    private static QueryOver<ResourceGrant, ResourceGrant> Subject(Guid vaultId, Guid? secretId, Guid? userId, Guid? groupId)
    {
        var query = QueryOver.Of<ResourceGrant>().Where(x => x.VaultId == vaultId);
        query = secretId is { } secret ? query.And(x => x.SecretId == secret) : query.And(x => x.SecretId == null);
        return userId is { } user ? query.And(x => x.UserId == user) : query.And(x => x.GroupId == groupId);
    }
}
