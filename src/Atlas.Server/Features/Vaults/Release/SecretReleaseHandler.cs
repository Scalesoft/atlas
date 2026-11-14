using System.Security.Claims;
using NHibernate;
using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Features.Vaults.References;
using Atlas.Server.Features.Vaults.Runtime;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Release;

/// <summary>
/// The database steps of a release (<see cref="SecretReleaseService"/> runs them around the file read). Every decision reads the
/// secret, its vault and the actor's grants afresh under the actor lock and the vault's space FOR SHARE (VaultAccess), nothing is
/// cached across requests, and every outcome on an existing secret writes SecretReleaseEvent rows (one per field, never a value)
/// that commit with the step. An unknown secret id is 404 without a row: there is nothing to reference.
/// </summary>
public sealed class SecretReleaseHandler
{
    private readonly ISession m_session;
    private readonly VaultAccess m_access;
    private readonly IReleasePolicy m_policy;
    private readonly VaultProtection m_protection;
    private readonly IHttpContextAccessor m_http;
    private readonly VaultFolders m_folders;

    public SecretReleaseHandler(ISession session, VaultAccess access, IReleasePolicy policy, VaultProtection protection, IHttpContextAccessor http, VaultFolders folders)
    {
        m_session = session;
        m_access = access;
        m_policy = policy;
        m_protection = protection;
        m_folders = folders;
        m_http = http;
    }

    public async Task<ReleaseStep> Handle(BeginSecretRelease command, CancellationToken ct)
    {
        var target = await m_access.ReadSecretAsync(command.SecretId, false, ct);
        if (await AuthorizeAsync(target, command.Fields, command.Operation, command.PageId, ct) is { } denied)
        {
            return denied;
        }
        var decision = await m_policy.EvaluateAsync(target.Actor, command.SecretId, Http(), ct);
        if (decision.RequiresReauth && (!decision.FreshAuthSupported || command.ProofId is null))
        {
            return await DenyReauthAsync(target, command.Fields, command.Operation, decision, command.PageId, ct);
        }
        var vault = target.Vault!;
        var location = await m_session.GetAsync<VaultLocation>(vault.LocationId, ct);
        try
        {
            var storage = VaultLocationRules.Storage(location, m_protection, m_folders);
            return new ReleaseStep(ReleaseOutcome.Continue, decision, new VaultTarget(vault.Id, storage, vault.FileName, vault.ProtectedKdbxPassword),
                target.Secret!.EntryUuid, Available(target, command.Fields));
        }
        catch (ApiException)
        {
            // The location password no longer decrypts: the file cannot be reached, as any other unavailable storage.
            await AuditAsync(target, "release", command.Fields, command.Operation, ReleaseResults.Unavailable, decision, false, command.PageId, ct);
            return new ReleaseStep(ReleaseOutcome.Unavailable, decision);
        }
    }

    public async Task<ReleaseStep> Handle(CompleteSecretRelease command, CancellationToken ct)
    {
        var target = await m_access.ReadSecretAsync(command.SecretId, false, ct);
        if (await AuthorizeAsync(target, command.Fields, command.Operation, command.PageId, ct) is { } denied)
        {
            return denied;
        }
        var http = Http();
        var decision = await m_policy.EvaluateAsync(target.Actor, command.SecretId, http, ct);
        var reauthUsed = false;
        if (decision.RequiresReauth)
        {
            // One request, one proof, whatever the number of fields.
            if (!decision.FreshAuthSupported || command.ProofId is not { } proofId
                || !await m_policy.TryConsumeProofAsync(target.Actor, http, command.SecretId, command.Operation, proofId, ct))
            {
                return await DenyReauthAsync(target, command.Fields, command.Operation, decision, command.PageId, ct);
            }
            reauthUsed = true;
        }
        await AuditAsync(target, "release", command.Fields, command.Operation, ReleaseResults.Released, decision, reauthUsed, command.PageId, ct);
        return new ReleaseStep(ReleaseOutcome.Released, decision, Fields: command.Fields);
    }

    public async Task Handle(RecordReleaseFailure command, CancellationToken ct)
    {
        var target = await m_access.ReadSecretAsync(command.SecretId, false, ct);
        if (target.Secret is not null)
        {
            await AuditAsync(target, "release", command.Fields, command.Operation, ReleaseResults.Unavailable, command.Decision, false, command.PageId, ct);
        }
    }

    public async Task Handle(ReportSecretRelease command, CancellationToken ct)
    {
        var fields = ReleaseFields.Parse(command.Fields, null);
        var operation = ReleaseFields.RequireOperation(command.Operation);
        var target = await m_access.ReadSecretAsync(command.SecretId, false, ct);
        target.Authorize(VaultAccessLevel.Read).Require();
        await AuditAsync(target, "client_report", fields, operation, operation == ReleaseFields.Copy ? "copied" : "shown", null, false, null, ct);
    }

    /// <summary>The requested fields the registry knows as non-empty.</summary>
    private static IReadOnlyList<string> Available(VaultAccessTarget target, IReadOnlyList<string> fields) =>
        fields.Where(name => (target.Secret!.AvailableFields & ReleaseFields.Flag(name)) != VaultSecretFields.None).ToArray();

    /// <summary>Čte on the secret now and at least one non-empty requested field; null when the release may continue.</summary>
    private async Task<ReleaseStep?> AuthorizeAsync(VaultAccessTarget target, IReadOnlyList<string> fields, string operation, Guid? pageId, CancellationToken ct)
    {
        if (target.Secret is null)
        {
            return new ReleaseStep(ReleaseOutcome.NotFound);
        }
        var authorization = target.Authorize(VaultAccessLevel.Read);
        if (!authorization.Allowed)
        {
            var missing = authorization.Outcome == VaultAccessOutcome.Missing;
            await AuditAsync(target, "release", fields, operation, missing ? ReleaseResults.NotFound : ReleaseResults.Forbidden, null, false, pageId, ct);
            return new ReleaseStep(missing ? ReleaseOutcome.NotFound : ReleaseOutcome.Forbidden);
        }
        if (Available(target, fields).Count == 0)
        {
            await AuditAsync(target, "release", fields, operation, ReleaseResults.NotFound, null, false, pageId, ct);
            return new ReleaseStep(ReleaseOutcome.FieldEmpty);
        }
        return null;
    }

    private async Task<ReleaseStep> DenyReauthAsync(VaultAccessTarget target, IReadOnlyList<string> fields, string operation, ReleaseDecision decision,
        Guid? pageId, CancellationToken ct)
    {
        var unsupported = !decision.FreshAuthSupported;
        await AuditAsync(target, "release", fields, operation, unsupported ? ReleaseResults.Unsupported : ReleaseResults.ReauthRequired, decision, false, pageId, ct);
        return new ReleaseStep(unsupported ? ReleaseOutcome.Unsupported : ReleaseOutcome.ReauthRequired, decision);
    }

    private HttpContext Http() => m_http.HttpContext ?? new DefaultHttpContext();

    private async Task AuditAsync(VaultAccessTarget target, string kind, IReadOnlyList<string> fields, string operation, string result,
        ReleaseDecision? decision, bool reauthUsed, Guid? pageId, CancellationToken ct)
    {
        var http = m_http.HttpContext;
        var page = pageId is { } requested ? await ConfirmedPageAsync(target, requested, ct) : null;
        // Every fact joins the access audit feed: a client report, a release or a refused one.
        var action = kind == "client_report" ? "secret.client_report" : result == ReleaseResults.Released ? "secret.released" : "secret.release_denied";
        foreach (var field in fields)
        {
            var id = await m_session.CreateSQLQuery("""
                INSERT INTO "SecretReleaseEvent"("Kind","ActorId","SecretId","VaultId","SpaceId","Field","Operation","Result","Mode","ModeSource",
                    "MatchedNetwork","ReauthUsed","SessionId","RequestId","ClientIp","PageId")
                VALUES (:kind,:actor,:secret,:vault,:space,:field,:operation,:result,:mode,:source,:network,:reauth,:session,:request,:ip,:page)
                RETURNING "Id"
                """)
                .SetParameter("kind", kind).SetParameter("actor", target.Actor.Id).SetParameter("secret", target.Secret!.Id)
                .SetParameter("vault", target.Vault!.Id).SetParameter("space", target.Vault.SpaceId)
                .SetParameter("field", field).SetParameter("operation", operation).SetParameter("result", result)
                .SetParameter("mode", decision?.Mode, NHibernateUtil.String).SetParameter("source", decision?.ModeSource, NHibernateUtil.String)
                .SetParameter("network", Truncate(decision?.MatchedNetwork, 200), NHibernateUtil.String).SetParameter("reauth", reauthUsed)
                .SetParameter("session", Truncate(http?.User.FindFirstValue("atlas_session"), 64), NHibernateUtil.String)
                .SetParameter("request", Truncate(http?.TraceIdentifier, 128), NHibernateUtil.String)
                .SetParameter("ip", http?.Connection.RemoteIpAddress?.ToString(), NHibernateUtil.String)
                .SetParameter("page", page, NHibernateUtil.Guid)
                .UniqueResultAsync<long>(ct);
            await AccessAuditLog.RecordAsync(m_session, target.Actor.Id, action, AccessAuditLog.SecretReleaseEvent, id, ct);
        }
    }

    /// <summary>
    /// The page the client says the reference was shown on, kept only when the server confirms it: the account can read the page
    /// (a reader its published revision, an editor also the working one and the live collaboration) and that content references the
    /// secret. Anything else is null, never an error: the page only attributes a release in Čtenost stránky, it never decides one.
    /// </summary>
    private async Task<Guid?> ConfirmedPageAsync(VaultAccessTarget target, Guid pageId, CancellationToken ct)
    {
        var contents = await m_session.CreateSQLQuery("""
            SELECT r."ContentJson" FROM "Page" p
            JOIN "EffectivePageAccess" m ON m."PageId" = p."Id" AND m."UserId" = :actor
            JOIN "PageRevision" r ON r."PageId" = p."Id"
                AND ((r."Id" = p."PublishedRevisionId" AND m."Role" >= 1) OR (r."ActiveTo" IS NULL AND m."Role" >= 2))
            WHERE p."Id" = :page AND NOT p."IsTrashed"
            """).SetParameter("actor", target.Actor.Id).SetParameter("page", pageId).ListAsync<string>(ct);
        if (contents.Any(json => SecretReferenceAccess.ReferencesOf(json).Contains(target.Secret!.Id)))
        {
            return pageId;
        }
        var live = await m_session.CreateSQLQuery("""
            SELECT count(*) FROM "CollaborationSecretReference" c
            JOIN "Page" p ON p."Id" = c."PageId" AND NOT p."IsTrashed"
            JOIN "EffectivePageAccess" m ON m."PageId" = c."PageId" AND m."UserId" = :actor AND m."Role" >= 2
            WHERE c."PageId" = :page AND c."SecretId" = :secret
            """).SetParameter("actor", target.Actor.Id).SetParameter("page", pageId).SetParameter("secret", target.Secret!.Id)
            .UniqueResultAsync<long>(ct);
        return live > 0 ? pageId : null;
    }

    private static string? Truncate(string? value, int maximum) => value is { Length: > 0 } text && text.Length > maximum ? text[..maximum] : value;
}
