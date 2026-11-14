using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Requests;

/// <summary>
/// The active accounts that manage a vault or one of its secrets now, as VaultGrantSnapshot decides it: every WikiAdmin, every
/// current space Admin of the vault's space and every current Spravuje grant on the vault (or on the secret), direct or through a
/// current group. Used only to address live notifications; authorization never relies on it.
/// </summary>
public static class VaultManagers
{
    public static async Task<IReadOnlyList<Guid>> ResolveAsync(ISession session, Guid spaceId, Guid vaultId, Guid? secretId, CancellationToken ct)
    {
        var ids = await session.CreateSQLQuery("""
            WITH subjects AS (
                SELECT g."UserId", g."GroupId" FROM "EffectiveSpaceGrant" g WHERE g."SpaceId" = :space AND g."Role" = 3
                UNION ALL
                SELECT g."UserId", g."GroupId" FROM "ResourceGrant" g
                WHERE g."VaultId" = :vault AND g."Role" = 40 AND g."ActiveTo" IS NULL
                    AND (g."SecretId" IS NULL OR g."SecretId" = :secret)
                    AND (g."StartAt" IS NULL OR g."StartAt" <= statement_timestamp())
                    AND (g."ExpiresAt" IS NULL OR statement_timestamp() < g."ExpiresAt"))
            SELECT u."Id" FROM "UserAccount" u
            WHERE u."IsActive" AND (u."IsWikiAdmin"
                OR u."Id" IN (SELECT "UserId" FROM subjects WHERE "UserId" IS NOT NULL)
                OR u."Id" IN (SELECT m."UserId" FROM "EffectiveGroupMember" m JOIN subjects s ON s."GroupId" = m."GroupId" WHERE m."ActiveTo" IS NULL))
            """)
            .SetParameter("space", spaceId).SetParameter("vault", vaultId).SetParameter("secret", secretId ?? Guid.Empty)
            .ListAsync<Guid>(ct);
        return ids.ToArray();
    }
}
