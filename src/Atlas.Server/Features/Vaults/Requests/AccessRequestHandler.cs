using NHibernate;
using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Release;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Requests;

/// <summary>
/// Access requests (WP4 V5): request → approval → grant (spec 2 and 3.2). Whoever can Discover a vault or secret may ask for Čte
/// with a reason; a repeated request returns the pending one. A manager of the target (Spravuje: space Admins and WikiAdmin
/// included) approves or rejects; approval writes the grant through <see cref="VaultGrantHandler"/> (bounded delegation, security
/// change guard, CAS, history, audit and AccessChanges), 30 days by default and never longer than the approver's own authority.
/// Transitions are audited by the database with their actor; accessRequestChanged goes to the requester and the managers after
/// the commit. A rejection is not a deny: access held another way stays.
/// Fresh re-authentication (intended asymmetry): approving widens access, so its grant write passes the security change guard (a
/// proof or the ten-minute re-authentication window); rejecting and cancelling cannot widen anyone's access, so they need neither.
/// </summary>
public sealed class AccessRequestHandler
{
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(30);

    private readonly ISession m_session;
    private readonly VaultAccess m_access;
    private readonly IContentAccess m_content;
    private readonly VaultGrantSnapshots m_grants;
    private readonly VaultGrantHandler m_grantWriter;
    private readonly AccessChanges m_accessChanges;
    private readonly ISecurityEvents m_events;
    private readonly SecretReleaseWatch m_watch;

    public AccessRequestHandler(ISession session, VaultAccess access, IContentAccess content, VaultGrantSnapshots grants,
        VaultGrantHandler grantWriter, AccessChanges accessChanges, ISecurityEvents events, SecretReleaseWatch watch)
    {
        m_session = session;
        m_access = access;
        m_content = content;
        m_grants = grants;
        m_grantWriter = grantWriter;
        m_accessChanges = accessChanges;
        m_events = events;
        m_watch = watch;
    }

    public async Task<AccessRequestView> Handle(RequestVaultAccess command, CancellationToken ct)
    {
        var reason = Validate(command.Request);
        return await CreateAsync(await m_access.ReadVaultAsync(command.VaultId, false, ct), reason, ct);
    }

    public async Task<AccessRequestView> Handle(RequestSecretAccess command, CancellationToken ct)
    {
        var reason = Validate(command.Request);
        return await CreateAsync(await m_access.ReadSecretAsync(command.SecretId, false, ct), reason, ct);
    }

    public async Task<AccessRequestView> Handle(ApproveAccessRequest command, CancellationToken ct)
    {
        var (request, target) = await RequireManagedAsync(command.RequestId, true, ct);
        var authority = target.Authorize(VaultAccessLevel.Manage).Require();
        var now = DateTime.UtcNow;
        DateTime expiresAt;
        if (command.ExpiresAt is { } explicitEnd)
        {
            expiresAt = Temporal.TruncateToMicroseconds(explicitEnd.UtcDateTime);
            Rules.RequireFutureEnd(expiresAt, now);
        }
        else
        {
            // The default is bounded by the approver's own authority; an explicit later end is refused by the grant path.
            expiresAt = Temporal.TruncateToMicroseconds(authority.Until is { } until && until < now + DefaultValidity ? until : now + DefaultValidity);
        }
        var current = await m_session.QueryOver<ResourceGrant>()
            .Where(g => g.VaultId == request.VaultId && g.UserId == request.RequesterId && g.ActiveTo == null)
            .And(request.SecretId is { } secretId
                ? NHibernate.Criterion.Restrictions.Eq(nameof(ResourceGrant.SecretId), secretId)
                : NHibernate.Criterion.Restrictions.IsNull(nameof(ResourceGrant.SecretId)))
            .SingleOrDefaultAsync(ct);
        var held = current is null ? VaultAccessLevel.None : VaultAccessLevels.FromRole(current.Role);
        Guid grantId;
        DateTime? grantExpiresAt;
        if (current is not null && held >= VaultAccessLevel.Read && (current.StartAt is null || current.StartAt <= now)
            && (current.ExpiresAt is null || current.ExpiresAt >= expiresAt))
        {
            // The requester's current grant already covers the request: approving must not shorten or lower it.
            grantId = current.Id;
            grantExpiresAt = current.ExpiresAt;
        }
        else
        {
            var level = held > VaultAccessLevel.Read ? held : VaultAccessLevel.Read;
            grantId = (await m_grantWriter.ApplyAsync(target, request.RequesterId, null, level, null, expiresAt,
                current is null ? null : AdministrationVersion.Format(current.ActiveFrom), ct))!.Value;
            grantExpiresAt = expiresAt;
        }
        Decide(request, VaultAccessRequest.Approved, target.Actor, null);
        request.GrantId = grantId;
        request.GrantExpiresAt = grantExpiresAt;
        return await FinishAsync(request, target.Actor, ct);
    }

    public async Task<AccessRequestView> Handle(RejectAccessRequest command, CancellationToken ct)
    {
        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        if (note is { Length: > VaultAccessRequest.TextMaxLength })
        {
            throw new ApiException(400, "Poznámka smí mít nejvýše 500 znaků.");
        }
        var (request, target) = await RequireManagedAsync(command.RequestId, false, ct);
        target.Authorize(VaultAccessLevel.Manage).Require();
        // No security change guard here, by design: a rejection grants nothing, so it needs no fresh re-authentication (approval does).
        Decide(request, VaultAccessRequest.Rejected, target.Actor, note);
        // A rejection is not a deny; the re-check hides a released value only when the account really has no Čte left.
        m_accessChanges.AfterCommit(m_watch.AccessChangedAsync);
        return await FinishAsync(request, target.Actor, ct);
    }

    public async Task<AccessRequestView> Handle(CancelAccessRequest command, CancellationToken ct)
    {
        var actor = await m_content.LockActorAsync(null, ct);
        var request = await m_session.GetAsync<VaultAccessRequest>(command.RequestId, LockMode.Upgrade, ct);
        if (request is null || request.RequesterId != actor.Id)
        {
            throw new ApiException(404, "Žádost nebyla nalezena.");
        }
        RequirePending(request);
        Decide(request, VaultAccessRequest.Cancelled, actor, null);
        return await FinishAsync(request, actor, ct);
    }

    private async Task<AccessRequestView> CreateAsync(VaultAccessTarget target, string reason, CancellationToken ct)
    {
        target.Authorize(VaultAccessLevel.Discover).Require();
        if (target.Authorize(VaultAccessLevel.Read).Allowed)
        {
            throw new ApiException(409, target.Secret is null ? "Trezor už číst můžete." : "Heslo už číst můžete.", "access_already_granted");
        }
        var actor = target.Actor;
        var vault = target.Vault!;
        var secretId = target.Secret?.Id;
        var pending = await m_session.QueryOver<VaultAccessRequest>()
            .Where(r => r.RequesterId == actor.Id && r.VaultId == vault.Id && r.State == VaultAccessRequest.Pending)
            .And(secretId is { } id
                ? NHibernate.Criterion.Restrictions.Eq(nameof(VaultAccessRequest.SecretId), id)
                : NHibernate.Criterion.Restrictions.IsNull(nameof(VaultAccessRequest.SecretId)))
            .SingleOrDefaultAsync(ct);
        if (pending is not null)
        {
            return await AccessRequestReads.ReadAsync(m_session, m_grants, actor, pending.Id, ct);
        }
        var request = new VaultAccessRequest
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            SecretId = secretId,
            VaultSpaceId = vault.SpaceId,
            RequesterId = actor.Id,
            Level = (int)VaultAccessLevel.Read,
            Reason = reason,
            State = VaultAccessRequest.Pending,
            CreatedAt = DateTime.UtcNow
        };
        await m_session.SaveAsync(request, ct);
        try
        {
            await m_session.FlushAsync(ct);
        }
        catch (NHibernate.Exceptions.GenericADOException error) when (error.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // A concurrent identical request won the unique pending index.
            throw new ApiException(409, "Žádost už čeká na vyřízení. Obnovte stránku.", "access_request_pending");
        }
        return await FinishAsync(request, actor, ct);
    }

    /// <summary>The request and its target, the target read under its space lock (FOR UPDATE when a grant is written), the request
    /// row locked after it; 404 when the request is unknown or the actor may not even Discover its target.</summary>
    private async Task<(VaultAccessRequest Request, VaultAccessTarget Target)> RequireManagedAsync(Guid requestId, bool writeGrants, CancellationToken ct)
    {
        var owner = await m_session.QueryOver<VaultAccessRequest>().Where(r => r.Id == requestId)
            .Select(r => r.VaultId, r => r.SecretId).SingleOrDefaultAsync<object[]>(ct);
        if (owner is null)
        {
            await m_content.LockActorAsync(null, ct);
            throw new ApiException(404, "Žádost nebyla nalezena.");
        }
        var target = owner[1] is Guid secretId
            ? await m_access.ReadSecretAsync(secretId, writeGrants, ct)
            : await m_access.ReadVaultAsync((Guid)owner[0], writeGrants, ct);
        if (target.Authorize(VaultAccessLevel.Manage).Outcome == VaultAccessOutcome.Missing)
        {
            throw new ApiException(404, "Žádost nebyla nalezena.");
        }
        var request = await m_session.GetAsync<VaultAccessRequest>(requestId, LockMode.Upgrade, ct);
        RequirePending(request!);
        return (request!, target);
    }

    private static void RequirePending(VaultAccessRequest request)
    {
        if (request.State != VaultAccessRequest.Pending)
        {
            throw new ApiException(409, "Žádost už byla vyřízena.", "access_request_decided");
        }
    }

    private static void Decide(VaultAccessRequest request, string state, UserAccount actor, string? note)
    {
        request.State = state;
        request.DecidedBy = actor.Id;
        request.DecidedAt = DateTime.UtcNow;
        request.DecisionNote = note;
    }

    /// <summary>Audits the new or decided request (the requester creates and cancels, a manager approves or rejects: DecidedBy is the
    /// actor of every transition), reads the view back and schedules accessRequestChanged for the requester and the managers after the
    /// commit.</summary>
    private async Task<AccessRequestView> FinishAsync(VaultAccessRequest request, UserAccount actor, CancellationToken ct)
    {
        await AccessAuditLog.RecordAsync(m_session, request.DecidedBy ?? request.RequesterId,
            "access_request." + (request.State == VaultAccessRequest.Pending ? "created" : request.State), AccessAuditLog.AccessRequest, request.Id, ct);
        var managers = await VaultManagers.ResolveAsync(m_session, request.VaultSpaceId, request.VaultId, request.SecretId, ct);
        var recipients = managers.Append(request.RequesterId).Distinct().ToArray();
        var change = new AccessRequestChanged(request.Id, request.SecretId, request.VaultId, request.State);
        m_accessChanges.AfterCommit(() => m_events.AccessRequestChangedAsync(recipients, change));
        return await AccessRequestReads.ReadAsync(m_session, m_grants, actor, request.Id, ct);
    }

    private static string Validate(CreateAccessRequest request)
    {
        if (request.Level != "read")
        {
            throw new ApiException(400, "Požádat lze jen o čtení (level read).");
        }
        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length == 0 || reason.Length > VaultAccessRequest.TextMaxLength)
        {
            throw new ApiException(400, "Uveďte důvod žádosti (nejvýše 500 znaků).");
        }
        return reason;
    }
}
