using NHibernate;

namespace Atlas.Server.Platform.Identity;

/// <summary>Authority proved by a completed transaction. Null is unbounded; transport still rechecks revocation.</summary>
public sealed record DownloadLease(DateTime? Until)
{
    public DownloadLease Limit(DateTime? end) => new(Until is null ? end : end is null ? Until : Until < end ? Until : end);
}

public static class DownloadCredentials
{
    public static Guid? TokenId(HttpContext context, bool personal)
    {
        if (!personal) return null;
        return Guid.TryParse(context.User.FindFirst("api_token_id")?.Value, out var id) ? id
            : throw new ApiException(401, "Platnost osobního API tokenu během stahování skončila.");
    }

    /// <summary>Recheck the authenticated token's live read scope, channel rest, the owner's level and stamp without retaining the bearer
    /// secret. Downloads exist on /api/v1 only, so the channel is rest.</summary>
    public static async Task<DateTime?> ReadAsync(NHibernate.ISession session, UserAccount actor, Guid? tokenId, CancellationToken ct)
    {
        if (tokenId is null) return null;
        var until = await session.CreateSQLQuery("""
            SELECT t."ExpiresAt" FROM "PersonalApiToken" t
            JOIN "UserAccount" u ON u."Id"=t."UserId" AND u."IsActive" AND u."SecurityStamp"=t."SecurityStamp"
            WHERE t."Id"=:token AND t."UserId"=:actor AND t."SecurityStamp"=:stamp
              AND t."ActiveTo" IS NULL AND t."ExpiresAt">statement_timestamp()
              AND EXISTS (SELECT 1 FROM "PersonalApiTokenScope" s WHERE s."TokenId"=t."Id" AND s."Scope"='read')
              AND EXISTS (SELECT 1 FROM "PersonalApiTokenChannel" c WHERE c."TokenId"=t."Id" AND c."Channel"='rest')
              AND EXISTS (SELECT 1 FROM "UserApiPermission" p WHERE p."UserId"=t."UserId" AND p."ActiveTo" IS NULL AND p."Level"<>'none')
            """).AddScalar("ExpiresAt", NHibernateUtil.UtcDateTime).SetParameter("token", tokenId.Value)
            .SetParameter("actor", actor.Id).SetParameter("stamp", actor.SecurityStamp).UniqueResultAsync<DateTime?>(ct);
        return until ?? throw new ApiException(401, "Platnost osobního API tokenu během stahování skončila.");
    }
}
