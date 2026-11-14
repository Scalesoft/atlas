using Atlas.Server.Features.Administration;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Hub.Contracts;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Features.Hub.Persistence;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Hub.Queries;

public sealed record GetHubSettings;
public sealed record GetHubProfile;
public sealed record GetHubDirectoryConfiguration;
public sealed record HubDirectoryConfiguration(Guid RevisionId, string Version, string BaseUrl, string ProtectedToken)
{
    public override string ToString() => $"HubDirectoryConfiguration {{ RevisionId = {RevisionId}, Token = [REDACTED] }}";
}
public sealed record GetHubVerificationIdentities(Guid? UserId, bool SelfOnly = false);

public sealed class HubSettingsQueriesHandler(ISession session, AdministrationAccess access, IContentAccess contentAccess, AtlasOidcOptions oidc)
{
    public async Task<HubProfileView> Handle(GetHubProfile query, CancellationToken ct)
    {
        var actor = await contentAccess.LockActorAsync(null, ct);
        var config = await HubReads.CurrentAsync(session, ct);
        if (config is null) return new(null, null);
        var rows = await session.CreateSQLQuery("""
            SELECT CASE WHEN s."SnapshotStartedAt">=:revision AND s."SnapshotStartedAt"<=statement_timestamp() THEN s."UpstreamAccountState" ELSE 0 END AS "UpstreamAccountState",
                CASE WHEN s."SnapshotStartedAt">=:revision AND s."SnapshotStartedAt"<=statement_timestamp() THEN s."VerifiedUntil" END AS "VerifiedUntil",
                s."LastSuccessAt",s."LastAttemptAt",s."LastErrorCode"
            FROM "ExternalIdentity" e LEFT JOIN "HubIdentityState" s ON s."ExternalIdentityId"=e."Id" AND s."ConnectionId"=:connection
            WHERE e."UserId"=:user AND e."Issuer"=:issuer AND e."ActiveTo" IS NULL
            """).SetParameter("connection", config.ConnectionId).SetParameter("revision", config.ActiveFrom)
            .SetParameter("user", actor.Id).SetParameter("issuer", config.Issuer).SetMaxResults(2).ListAsync<object[]>(ct);
        if (rows.Count == 0) return new(config.Mode.ToString(), null);
        if (rows.Count != 1) return new(config.Mode.ToString(), new("Unverified", null, null, null, "identity-conflict"));
        var row = rows[0];
        return new(config.Mode.ToString(), new(row[0] is null ? "Unverified" : ((HubUpstreamAccountState)Convert.ToInt16(row[0])).ToString(),
            (DateTime?)row[1], (DateTime?)row[2], (DateTime?)row[3], (string?)row[4]));
    }

    public async Task<HubSettingsView> Handle(GetHubSettings query, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        return await HubSettingsReader.ReadAsync(session, oidc, ct);
    }

    public async Task<HubDirectoryConfiguration> Handle(GetHubDirectoryConfiguration query, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        var row = await HubReads.CurrentAsync(session, ct) ?? throw new ApiException(409, "Nejprve uložte připojení Hubu.");
        return new(row.Id, AdministrationVersion.Format(row.ActiveFrom), row.BaseUrl, row.ProtectedToken);
    }

    public async Task<IReadOnlyList<Guid>> Handle(GetHubVerificationIdentities query, CancellationToken ct)
    {
        var actor = query.SelfOnly ? await contentAccess.LockActorAsync(null, ct) : await access.RequireWikiAdminAsync(ct);
        var row = await HubReads.CurrentAsync(session, ct);
        if (row is null || row.Mode == HubConnectionMode.Disabled) throw new ApiException(409, "Ověřování je dostupné v režimu Preview nebo Enforced.");
        var userId = query.SelfOnly ? actor.Id : query.UserId;
        var hql = "select e.Id from ExternalIdentity e where e.ActiveTo is null and e.Issuer=:issuer" + (userId is null ? "" : " and e.UserId=:user") + " order by e.Id";
        var command = session.CreateQuery(hql).SetParameter("issuer", row.Issuer).SetMaxResults(1000);
        if (userId is not null) command.SetParameter("user", userId.Value);
        return (await command.ListAsync<Guid>(ct)).ToArray();
    }
}
