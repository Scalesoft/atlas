using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Hub.Contracts;
using Atlas.Server.Features.Hub.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Hub.Persistence;

public static class HubSettingsReader
{
    public static async Task<HubSettingsView> ReadAsync(ISession session, AtlasOidcOptions oidc, CancellationToken ct)
    {
        var current = await HubReads.CurrentAsync(session, ct);
        var providers = oidc.Providers.Select(x => new HubProviderView(x.Issuer, x.DisplayName)).ToArray();
        if (current is null) return new(null, null, "", "", false, "Disabled", providers, [], []);
        var bindings = await session.CreateSQLQuery("""
            SELECT b."GroupId",g."Name",b."ExternalGroupId",b."Enabled",b."GrantsWikiAdmin",r."ActiveFrom"
            FROM "HubGroupBinding" b JOIN "UserGroup" g ON g."Id"=b."GroupId"
            JOIN "HubGroupBindingRevision" r ON r."GroupId"=b."GroupId" AND r."ActiveTo" IS NULL
            WHERE b."ConnectionId"=:connection ORDER BY g."Name" LIMIT 1000
            """).SetParameter("connection", current.ConnectionId).ListAsync<object[]>(ct);
        var identities = await session.CreateSQLQuery("""
            SELECT e."UserId",u."DisplayName",e."Id",
                CASE WHEN s."SnapshotStartedAt">=:revision AND s."SnapshotStartedAt"<=statement_timestamp() THEN s."UpstreamAccountState" ELSE 0 END AS "UpstreamAccountState",
                CASE WHEN s."SnapshotStartedAt">=:revision AND s."SnapshotStartedAt"<=statement_timestamp() THEN s."VerifiedUntil" END AS "VerifiedUntil",
                s."LastSuccessAt",s."LastAttemptAt",s."LastErrorCode"
            FROM "ExternalIdentity" e JOIN "UserAccount" u ON u."Id"=e."UserId"
            LEFT JOIN "HubIdentityState" s ON s."ExternalIdentityId"=e."Id" AND s."ConnectionId"=:connection
            WHERE e."Issuer"=:issuer AND e."ActiveTo" IS NULL ORDER BY u."DisplayName",e."Id" LIMIT 1000
            """).SetParameter("connection", current.ConnectionId).SetParameter("revision", current.ActiveFrom)
            .SetParameter("issuer", current.Issuer).ListAsync<object[]>(ct);
        return new(current.ConnectionId, AdministrationVersion.Format(current.ActiveFrom), current.Issuer, current.BaseUrl,
            current.ProtectedToken.Length != 0, current.Mode.ToString(), providers,
            bindings.Select(x => new HubBindingView((Guid)x[0], (string)x[1], (string)x[2], (bool)x[3], (bool)x[4],
                AdministrationVersion.Format((DateTime)x[5]), GroupUrl(current.BaseUrl, (string)x[2]))).ToArray(),
            identities.Select(x => new HubIdentityView((Guid)x[0], (string)x[1], (Guid)x[2],
                x[3] is null ? "Unverified" : ((HubUpstreamAccountState)Convert.ToInt16(x[3])).ToString(),
                (DateTime?)x[4], (DateTime?)x[5], (DateTime?)x[6], (string?)x[7])).ToArray(),
            Health: await HealthAsync(session, current, ct));
    }

    public static async Task<HubHealthView> HealthAsync(ISession session, HubConnectionRevision config, CancellationToken ct)
    {
        var row = await session.CreateSQLQuery("""
            SELECT cast(count(*) AS integer) AS "Known",
                cast(count(*) FILTER (WHERE s."VerifiedUntil" IS NULL OR s."SnapshotStartedAt"<:revision OR s."SnapshotStartedAt">statement_timestamp()) AS integer) AS "Pending",
                cast(count(*) FILTER (WHERE s."VerifiedUntil"<=statement_timestamp()) AS integer) AS "Expired",
                cast(count(*) FILTER (WHERE s."LastErrorCode" IS NOT NULL) AS integer) AS "Errors",max(s."LastSuccessAt") AS "LastSuccessAt"
            FROM "ExternalIdentity" e JOIN "UserAccount" u ON u."Id"=e."UserId" AND u."IsActive"
            LEFT JOIN "HubIdentityState" s ON s."ExternalIdentityId"=e."Id" AND s."ConnectionId"=:connection
            WHERE e."Issuer"=:issuer AND e."ActiveTo" IS NULL
            """).SetParameter("connection", config.ConnectionId).SetParameter("issuer", config.Issuer)
            .SetParameter("revision", config.ActiveFrom).UniqueResultAsync<object[]>(ct);
        var known = (int)row[0];
        var pending = (int)row[1];
        var expired = (int)row[2];
        var errors = (int)row[3];
        var state = config.Mode == HubConnectionMode.Disabled ? "Disabled" : expired > 0 ? "Stale" :
            errors > 0 ? "Error" : pending > 0 || known == 0 ? "Unverified" : "Healthy";
        return new(config.ConnectionId, state, known, pending, expired, errors, (DateTime?)row[4]);
    }

    public static string GroupUrl(string baseUrl, string id) => baseUrl.TrimEnd('/') + "/groups/" + Uri.EscapeDataString(id);

    public static async Task RequireRecoveryAsync(ISession session, AtlasOidcOptions oidc, CancellationToken ct)
    {
        var count = await session.CreateQuery("""
            select count(u.Id) from UserAccount u, BuiltinCredential c where c.UserId=u.Id and u.IsActive=true and u.IsWikiAdmin=true
            and u.OidcAdminIssuer is null and c.PasswordHash is not null and length(c.PasswordHash)>0
            """).UniqueResultAsync<long>(ct);
        if (!oidc.BuiltinEnabled || count == 0) throw new ApiException(409, "Před zapnutím Hub oprávnění zachovejte aktivního místního správce s heslem a povolené místní přihlášení.");
    }

    public static Task<DateTime?> BindingVersionAsync(ISession session, Guid groupId, CancellationToken ct) => session.CreateSQLQuery(
        "SELECT \"ActiveFrom\" FROM \"HubGroupBindingRevision\" WHERE \"GroupId\"=:id AND \"ActiveTo\" IS NULL")
        .SetParameter("id", groupId).UniqueResultAsync<DateTime?>(ct);
}
