using System.Globalization;
using System.Text.Json;
using NHibernate;
using Atlas.Server.Features.Vaults.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Queries;

/// <summary>
/// Who has which level on one vault or secret and why, mirroring PageAccessDirectory. Every source is a leg of the decision
/// VaultGrantSnapshot makes: a space Admin grant of the vault's space (EffectiveSpaceGrant, manage), the vault's grants
/// and, for a secret, the secret's grants, with the same statement-clock validity; current grants outside their validity are
/// listed as not effective. Every active WikiAdmin has a wikiAdmin source (manage everywhere). Each read is one SQL statement; callers hold the vault's space lock. Every
/// result column has its own alias, because NHibernate maps native SQL scalars by column name.
/// </summary>
internal static class VaultAccessDirectory
{
    public const int PeopleLimit = 500;
    private const int GroupLimit = 200;

    /// <summary>Active people with a source, managers first; the list shown to managers. <paramref name="granted"/> keeps only people
    /// with a vault or secret grant source: the security centre shows WikiAdmins and space Admins in its locked rows instead.</summary>
    public static async Task<(IReadOnlyList<VaultAccessPerson> Items, bool HasMore)> PeopleAsync(ISession session, Guid spaceId, Guid vaultId,
        Guid? secretId, CancellationToken ct, bool granted = false, int limit = PeopleLimit)
    {
        var rows = await Query(session, spaceId, vaultId, secretId, SourcesSql + PeopleSql + """
                u."IsActive" AND s."UserId" IS NOT NULL
                AND (NOT :granted OR EXISTS (SELECT 1 FROM sources x WHERE x."UserId" = u."Id" AND x."Kind" IN ('vault', 'secret')))
            ORDER BY s."Level" DESC, lower(u."DisplayName"), lower(u."Username"), u."Id"
            """).SetParameter("granted", granted).SetMaxResults(limit + 1).ListAsync<object[]>(ct);
        return (rows.Take(limit).Select(Person).ToArray(), rows.Count > limit);
    }

    /// <summary>One account's row, also without any source (no level). An inactive account has no source.</summary>
    public static async Task<VaultAccessPerson> PersonAsync(ISession session, Guid spaceId, Guid vaultId, Guid? secretId, Guid userId,
        CancellationToken ct)
    {
        var rows = await Query(session, spaceId, vaultId, secretId, SourcesSql + PeopleSql + " u.\"Id\" = :user")
            .SetParameter("user", userId).ListAsync<object[]>(ct);
        return Person(rows[0]);
    }

    /// <summary>Groups with a grant applying to the target, with their active member count.</summary>
    public static async Task<IReadOnlyList<VaultAccessGroup>> GroupsAsync(ISession session, Guid spaceId, Guid vaultId, Guid? secretId,
        CancellationToken ct)
    {
        var rows = await Query(session, spaceId, vaultId, secretId, """
            SELECT ug."Id", ug."Name",
                (SELECT count(*) FROM "EffectiveGroupMember" m JOIN "UserAccount" u ON u."Id" = m."UserId" AND u."IsActive"
                    WHERE m."GroupId" = ug."Id" AND m."ActiveTo" IS NULL) AS "MemberCount",
                src."Level", src."Kind", src."GrantId",
            """ + Utc("src.\"StartAt\"") + " AS \"StartAt\", " + Utc("src.\"ExpiresAt\"") + " AS \"ExpiresAt\", " + Utc("src.\"ActiveFrom\"") + """
                 AS "ActiveFrom", src."Effective"
            FROM (
                SELECT g."GroupId", 40 AS "Level", 'spaceAdmin' AS "Kind", g."Id" AS "GrantId", g."StartAt", g."ExpiresAt",
                    CAST(NULL AS timestamptz) AS "ActiveFrom", true AS "Effective"
                FROM "EffectiveSpaceGrant" g
                WHERE g."SpaceId" = :space AND g."Role" = 3 AND g."GroupId" IS NOT NULL
                UNION ALL
                SELECT g."GroupId", g."Role", CASE WHEN g."SecretId" IS NULL THEN 'vault' ELSE 'secret' END, g."Id", g."StartAt",
                    g."ExpiresAt", g."ActiveFrom",
            """ + EffectiveGrant + """
                FROM "ResourceGrant" g
                WHERE g."VaultId" = :vault AND g."ActiveTo" IS NULL AND g."GroupId" IS NOT NULL
                    AND (g."SecretId" IS NULL OR g."SecretId" = :secret)
            ) src
            JOIN "UserGroup" ug ON ug."Id" = src."GroupId"
            ORDER BY lower(ug."Name"), ug."Id", CASE src."Kind" WHEN 'spaceAdmin' THEN 0 WHEN 'vault' THEN 1 ELSE 2 END
            """).SetMaxResults(GroupLimit).ListAsync<object[]>(ct);
        return rows.GroupBy(row => (Guid)row[0]).Select(group =>
        {
            var first = group.First();
            var sources = group.Select(row => Source(new SourceRow((string)row[4], Convert.ToInt32(row[3], CultureInfo.InvariantCulture),
                group.Key, (string)row[1], row[5] as Guid?, Utc(row[6]), Utc(row[7]), Utc(row[8]), (bool)row[9]))).ToArray();
            return new VaultAccessGroup(group.Key, (string)first[1], Convert.ToInt32(first[2], CultureInfo.InvariantCulture),
                Highest(group.Where(row => (bool)row[9]).Select(row => Convert.ToInt32(row[3], CultureInfo.InvariantCulture))), sources);
        }).ToArray();
    }

    /// <summary>The latest audit event of a grant applying to the target (the vault's grants, and the secret's own for a secret).</summary>
    public static async Task<VaultAccessChange?> LastChangeAsync(ISession session, Guid vaultId, Guid? secretId, CancellationToken ct)
    {
        var row = await session.CreateSQLQuery("""
            SELECT e."OccurredAt", e."Action", e."ActorId", a."DisplayName" AS "ActorName"
            FROM "ResourceGrant" g
            JOIN "AccessAuditEvent" e ON e."ResourceGrantId" = g."Id"
            LEFT JOIN "UserAccount" a ON a."Id" = e."ActorId"
            WHERE g."VaultId" = :vault AND (g."SecretId" IS NULL OR g."SecretId" = :secret)
            ORDER BY e."Id" DESC
            LIMIT 1
            """).SetParameter("vault", vaultId).SetParameter("secret", secretId, NHibernateUtil.Guid).UniqueResultAsync<object[]?>(ct);
        return row is null ? null : new VaultAccessChange(DateTime.SpecifyKind((DateTime)row[0], DateTimeKind.Utc), (string)row[1],
            row[2] as Guid?, row[3] as string);
    }

    private static IQuery Query(ISession session, Guid spaceId, Guid vaultId, Guid? secretId, string sql)
    {
        return session.CreateSQLQuery(sql).SetParameter("space", spaceId).SetParameter("vault", vaultId)
            .SetParameter("secret", secretId, NHibernateUtil.Guid);
    }

    private static VaultAccessPerson Person(object[] row)
    {
        var sources = JsonSerializer.Deserialize<SourceRow[]>((string)row[4])!.Select(Source).ToArray();
        return new VaultAccessPerson((Guid)row[0], (string)row[1], (string)row[2],
            VaultAccessLevels.FromRole(Convert.ToInt32(row[3], CultureInfo.InvariantCulture)).Name(), sources);
    }

    private static VaultAccessSource Source(SourceRow row)
    {
        return new VaultAccessSource(row.Kind, row.GroupName ?? "", VaultAccessLevels.FromRole(row.Level).Name()!,
            row.GroupId, row.GrantId, row.StartAt, row.ExpiresAt,
            row.Kind != "spaceAdmin" && row.ActiveFrom is { } version ? AdministrationVersion.Format(version) : null, row.Effective);
    }

    private static string? Highest(IEnumerable<int> levels)
    {
        return VaultAccessLevels.FromRole(levels.DefaultIfEmpty().Max()).Name();
    }

    private static DateTime? Utc(object? value) => value is string text
        ? DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
        : null;

    /// <summary>A timestamp as exact UTC text (microseconds), independent of the session time zone, as PageAccessDirectory.</summary>
    private static string Utc(string column) => $"to_char({column} AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"')";

    private sealed record SourceRow(string Kind, int Level, Guid? GroupId, string? GroupName, Guid? GrantId, DateTime? StartAt,
        DateTime? ExpiresAt, DateTime? ActiveFrom, bool Effective);

    private const string EffectiveGrant = """
        (g."StartAt" IS NULL OR g."StartAt" <= statement_timestamp()) AND (g."ExpiresAt" IS NULL OR statement_timestamp() < g."ExpiresAt")
        """;

    // Parameters :space (the vault's space), :vault and :secret (null for the vault itself). One row per (account, source); a group
    // grant expands to the group's current members. Vault and secret grants: every current row, Effective only inside its validity.
    private static readonly string SourcesSql = """
        WITH sources AS (
            SELECT u."Id" AS "UserId", 40 AS "Level", 'wikiAdmin' AS "Kind", CAST(NULL AS uuid) AS "GroupId", CAST(NULL AS uuid) AS "GrantId",
                CAST(NULL AS timestamptz) AS "StartAt", CAST(NULL AS timestamptz) AS "ExpiresAt", CAST(NULL AS timestamptz) AS "ActiveFrom",
                true AS "Effective"
            FROM "UserAccount" u WHERE u."IsWikiAdmin"
            UNION ALL
            SELECT g."UserId", 40 AS "Level", 'spaceAdmin' AS "Kind", CAST(NULL AS uuid) AS "GroupId", g."Id" AS "GrantId",
                g."StartAt", g."ExpiresAt", CAST(NULL AS timestamptz) AS "ActiveFrom", true AS "Effective"
            FROM "EffectiveSpaceGrant" g
            WHERE g."SpaceId" = :space AND g."Role" = 3 AND g."UserId" IS NOT NULL
            UNION ALL
            SELECT m."UserId", 40, 'spaceAdmin', g."GroupId", g."Id", g."StartAt", LEAST(g."ExpiresAt", m."VerifiedUntil"), NULL, true
            FROM "EffectiveSpaceGrant" g
            JOIN "EffectiveGroupMember" m ON m."GroupId" = g."GroupId" AND m."ActiveTo" IS NULL
            WHERE g."SpaceId" = :space AND g."Role" = 3
            UNION ALL
            SELECT COALESCE(g."UserId", m."UserId"), g."Role", CASE WHEN g."SecretId" IS NULL THEN 'vault' ELSE 'secret' END, g."GroupId",
                g."Id", g."StartAt", LEAST(g."ExpiresAt", m."VerifiedUntil"), g."ActiveFrom",
        """ + EffectiveGrant + """
            FROM "ResourceGrant" g
            LEFT JOIN "EffectiveGroupMember" m ON m."GroupId" = g."GroupId" AND m."ActiveTo" IS NULL
            WHERE g."VaultId" = :vault AND g."ActiveTo" IS NULL AND (g."SecretId" IS NULL OR g."SecretId" = :secret)
                AND (g."UserId" IS NOT NULL OR m."UserId" IS NOT NULL)
        ), summary AS (
            SELECT s."UserId", COALESCE(max(s."Level") FILTER (WHERE s."Effective"), 0) AS "Level",
                CAST(json_agg(json_build_object('Kind', s."Kind", 'Level', s."Level", 'GroupId', s."GroupId",
                    'GroupName', ug."Name", 'GrantId', s."GrantId", 'StartAt',
        """ + Utc("s.\"StartAt\"") + ", 'ExpiresAt', " + Utc("s.\"ExpiresAt\"") + ", 'ActiveFrom', " + Utc("s.\"ActiveFrom\"") + """
                    , 'Effective', s."Effective") ORDER BY s."Effective" DESC,
                        CASE s."Kind" WHEN 'wikiAdmin' THEN 0 WHEN 'spaceAdmin' THEN 1 WHEN 'vault' THEN 2 ELSE 3 END, ug."Name") AS text) AS "Sources"
            FROM sources s LEFT JOIN "UserGroup" ug ON ug."Id" = s."GroupId"
            GROUP BY s."UserId"
        )
        """;

    private const string PeopleSql = """
        SELECT u."Id", u."DisplayName", u."Username", COALESCE(s."Level", 0) AS "Level", COALESCE(s."Sources", '[]') AS "Sources"
        FROM "UserAccount" u
        LEFT JOIN summary s ON s."UserId" = u."Id" AND u."IsActive"
        WHERE
        """;
}
