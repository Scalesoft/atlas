using System.Text.Json;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageAccess.Queries;

/// <summary>
/// Who reaches one page and why, for the access tab (WP3 S5). Every source is a leg of the EffectivePageAccess view (WikiAdmin,
/// space defaults unless own access, the space Admin on every page, direct page grants) with the same statement-clock
/// validity, so the effective role agrees with ResourceAccessPolicy; current page grants outside their validity are listed as
/// not effective so a manager can change them. Each read is one SQL statement (no per-person queries); callers hold the page's
/// space lock and pass the page read under it. Every result column has its own alias: NHibernate maps native SQL scalars by
/// column name, so two unnamed count(*) columns would both read the first.
/// </summary>
internal static class PageAccessDirectory
{
    private const int GroupLimit = 200;
    private const int LosingNames = 10;

    /// <summary>People with a source, paged, filtered by name or username; the list shown to managers. <paramref name="granted"/>
    /// keeps only people with a space default or page grant source: the security centre shows WikiAdmins and space Admins in its
    /// locked rows instead.</summary>
    public static async Task<(IReadOnlyList<AccessPerson> Items, bool HasMore)> PeopleAsync(ISession session, Page page, string term,
        int offset, int limit, CancellationToken ct, bool granted = false)
    {
        var rows = await Query(session, page, SourcesSql + PeopleSql + """
                u."IsActive" AND s."UserId" IS NOT NULL
                AND (:term = '' OR strpos(lower(u."DisplayName"), :term) > 0 OR strpos(lower(u."Username"), :term) > 0)
                AND (NOT :granted OR EXISTS (SELECT 1 FROM sources x WHERE x."UserId" = u."Id" AND x."Kind" IN ('space', 'page')))
            ORDER BY s."Role" DESC NULLS LAST, lower(u."DisplayName"), lower(u."Username"), u."Id"
            """)
            .SetParameter("term", term.ToLowerInvariant()).SetParameter("granted", granted)
            .SetFirstResult(offset).SetMaxResults(limit + 1).ListAsync<object[]>(ct);
        return (rows.Take(limit).Select(row => Person(row, page)).ToArray(), rows.Count > limit);
    }

    /// <summary>One account's row, also without any source (then Role is null and Sources empty) and also for an inactive account
    /// (no source applies to it then: the policy denies an inactive principal). Null for an unknown account.</summary>
    public static async Task<AccessPerson?> PersonAsync(ISession session, Page page, Guid userId, CancellationToken ct)
    {
        var rows = await Query(session, page, SourcesSql + PeopleSql + " u.\"Id\" = :user")
            .SetParameter("user", userId).ListAsync<object[]>(ct);
        return rows.Count == 0 ? null : Person(rows[0], page);
    }

    /// <summary>Groups with a grant applying to the page, with their active member count, filtered by name.</summary>
    public static async Task<IReadOnlyList<AccessGroup>> GroupsAsync(ISession session, Page page, string term, CancellationToken ct)
    {
        var rows = await Query(session, page, """
            SELECT ug."Id", ug."Name",
                (SELECT count(*) FROM "EffectiveGroupMember" m JOIN "UserAccount" u ON u."Id" = m."UserId" AND u."IsActive"
                    WHERE m."GroupId" = ug."Id" AND m."ActiveTo" IS NULL) AS "MemberCount",
                src."Role", src."Kind", src."GrantId",
            """ + Utc("src.\"StartAt\"") + " AS \"StartAt\", " + Utc("src.\"ExpiresAt\"") + " AS \"ExpiresAt\", " + Utc("src.\"ActiveFrom\"") + """
                 AS "ActiveFrom", src."Effective"
            FROM (
                SELECT g."GroupId", g."Role", CASE WHEN g."Role" = 3 THEN 'spaceAdmin' ELSE 'space' END AS "Kind", g."Id" AS "GrantId",
                    g."StartAt", g."ExpiresAt", CAST(NULL AS timestamptz) AS "ActiveFrom", true AS "Effective"
                FROM "EffectiveSpaceGrant" g
                WHERE g."SpaceId" = :space AND g."GroupId" IS NOT NULL AND (NOT :own OR g."Role" = 3)
                UNION ALL
                SELECT g."GroupId", g."Role", 'page', g."Id", g."StartAt", g."ExpiresAt", g."ActiveFrom",
            """ + EffectiveGrant + """
                FROM "ResourceGrant" g
                WHERE g."PageId" = :page AND g."ActiveTo" IS NULL AND g."GroupId" IS NOT NULL
            ) src
            JOIN "UserGroup" ug ON ug."Id" = src."GroupId"
            WHERE :term = '' OR strpos(lower(ug."Name"), :term) > 0
            ORDER BY lower(ug."Name"), ug."Id", src."Role" DESC, src."Kind"
            """)
            .SetParameter("term", term.ToLowerInvariant())
            .SetMaxResults(GroupLimit).ListAsync<object[]>(ct);
        return rows.GroupBy(row => (Guid)row[0]).Select(group =>
        {
            var first = group.First();
            var sources = group.Select(row => Source(new SourceRow((string)row[4], Convert.ToInt32(row[3]), group.Key, (string)row[1],
                row[5] as Guid?, Utc(row[6]), Utc(row[7]), Utc(row[8]), (bool)row[9]))).ToArray();
            return new AccessGroup(group.Key, (string)first[1], Convert.ToInt32(first[2]), MaxRole(sources), sources);
        }).ToArray();
    }

    /// <summary>Role counts and the impact of a mode change, in one statement.</summary>
    public static async Task<PageAccessCounts> CountsAsync(ISession session, Page page, CancellationToken ct)
    {
        var row = await Query(session, page, SourcesSql + ImpactSql + """
            SELECT count(*) FILTER (WHERE p."Current" = 3) AS "Manage", count(*) FILTER (WHERE p."Current" = 2) AS "Edit",
                count(*) FILTER (WHERE p."Current" = 1) AS "Read",
                count(*) FILTER (WHERE p."Current" IS NULL AND p."Default" IS NOT NULL) AS "Hidden",
                count(*) FILTER (WHERE p."Current" IS NOT NULL AND p."Default" IS NULL) AS "Lose",
                count(*) FILTER (WHERE p."Current" > p."Default") AS "Downgrade",
                (SELECT count(*) FROM "SpaceGrant" g WHERE g."SpaceId" = :space AND g."ActiveTo" IS NULL AND g."Role" < 3
                    AND (g."ExpiresAt" IS NULL OR statement_timestamp() < g."ExpiresAt")) AS "DefaultGrants",
                COALESCE((SELECT CAST(json_agg(n."DisplayName") AS text) FROM (
                    SELECT u."DisplayName" FROM people l JOIN "UserAccount" u ON u."Id" = l."UserId"
                    WHERE l."Current" IS NOT NULL AND l."Default" IS NULL
                    ORDER BY lower(u."DisplayName"), u."Id" LIMIT :names) n), '[]') AS "Losing"
            FROM people p
            """).SetParameter("names", LosingNames).UniqueResultAsync<object[]>(ct);
        return new PageAccessCounts(Convert.ToInt32(row[0]), Convert.ToInt32(row[1]), Convert.ToInt32(row[2]), Convert.ToInt32(row[3]),
            Convert.ToInt32(row[4]), Convert.ToInt32(row[5]), Convert.ToInt32(row[6]),
            JsonSerializer.Deserialize<string[]>((string)row[7])!);
    }

    /// <summary>The highest role an active account holds from the space defaults alone (what it would have without own access
    /// and without page grants); null when none.</summary>
    public static async Task<int?> DefaultRoleAsync(ISession session, Page page, Guid userId, CancellationToken ct)
    {
        var role = await Query(session, page, SourcesSql + ImpactSql + """
            SELECT p."Default" FROM people p WHERE p."UserId" = :user
            """).SetParameter("user", userId).UniqueResultAsync<object?>(ct);
        return role is null ? null : Convert.ToInt32(role);
    }

    public static async Task<ShareLinkSummary> ShareLinksAsync(ISession session, Page page, CancellationToken ct)
    {
        var row = await session.CreateSQLQuery("""
            SELECT count(*) AS "Active", min(l."ExpiresAt") AS "NextExpiry"
            FROM "PageShareLink" l
            JOIN "SpaceExternalSharingPolicy" p ON p."Id" = l."PolicyId" AND p."Enabled" AND p."ActiveTo" IS NULL
            WHERE l."PageId" = :page AND l."SpaceId" = :space AND l."ActiveTo" IS NULL AND statement_timestamp() < l."ExpiresAt"
            """).SetParameter("page", page.Id).SetParameter("space", page.SpaceId).UniqueResultAsync<object[]>(ct);
        var active = page.PublishedRevisionId is null || page.IsTrashed ? 0 : Convert.ToInt32(row[0]);
        return new ShareLinkSummary(active, active == 0 ? null : row[1] as DateTime?);
    }

    /// <summary>Active users and groups by name for the add wizard and "Zobrazit jako"; a bounded directory for page managers.</summary>
    public static async Task<IReadOnlyList<AccessCandidate>> CandidatesAsync(ISession session, string kind, string term, int limit,
        CancellationToken ct)
    {
        var sql = kind == "group"
            ? """
              SELECT g."Id", g."Name", CAST((SELECT count(*) FROM "EffectiveGroupMember" m JOIN "UserAccount" u ON u."Id" = m."UserId" AND u."IsActive"
                  WHERE m."GroupId" = g."Id" AND m."ActiveTo" IS NULL) AS text) AS "Detail"
              FROM "UserGroup" g WHERE :term = '' OR strpos(lower(g."Name"), :term) > 0
              ORDER BY lower(g."Name"), g."Id"
              """
            : """
              SELECT u."Id", u."DisplayName" AS "Name", u."Username" AS "Detail" FROM "UserAccount" u
              WHERE u."IsActive" AND (:term = '' OR strpos(lower(u."DisplayName"), :term) > 0 OR strpos(lower(u."Username"), :term) > 0)
              ORDER BY lower(u."DisplayName"), lower(u."Username"), u."Id"
              """;
        var rows = await session.CreateSQLQuery(sql).SetParameter("term", term.ToLowerInvariant()).SetMaxResults(limit).ListAsync<object[]>(ct);
        return rows.Select(row => new AccessCandidate((Guid)row[0], kind, (string)row[1], row[2] as string)).ToArray();
    }

    private static IQuery Query(ISession session, Page page, string sql)
    {
        return session.CreateSQLQuery(sql).SetParameter("page", page.Id).SetParameter("space", page.SpaceId).SetParameter("own", page.OwnAccess);
    }

    private static AccessPerson Person(object[] row, Page page)
    {
        var id = (Guid)row[0];
        var wikiAdmin = (bool)row[3];
        int? role = row[4] is null ? null : Convert.ToInt32(row[4]);
        var sources = JsonSerializer.Deserialize<SourceRow[]>((string)row[5])!.Select(Source).ToArray();
        var published = page.PublishedRevisionId is not null;
        // The private draft rule (PageVisibility): an unpublished page is visible to its owner and WikiAdmin only.
        var canEdit = role >= 2 && (published || wikiAdmin || page.OwnerId == id);
        return new AccessPerson(id, (string)row[1], (string)row[2], RoleName(role), wikiAdmin, published && role >= 1, canEdit, canEdit,
            sources, row[6] as DateTime?, row[7] as DateTime?);
    }

    private static AccessSource Source(SourceRow row)
    {
        return new AccessSource(row.Kind, row.GroupName ?? "", RoleName(row.Role)!, row.GroupId, row.GrantId, row.StartAt, row.ExpiresAt,
            row.Kind == "page" && row.ActiveFrom is { } version ? AdministrationVersion.Format(version) : null, row.Effective);
    }

    private static string? MaxRole(IEnumerable<AccessSource> sources)
    {
        var roles = sources.Where(s => s.Effective).Select(s => Array.IndexOf(Roles, s.Role)).ToArray();
        return roles.Length == 0 ? null : Roles[roles.Max()];
    }

    private static readonly string[] Roles = ["", "Reader", "Writer", "Admin"];

    internal static string? RoleName(int? role) => role is >= 1 and <= 3 ? Roles[role.Value] : null;

    private static DateTime? Utc(object? value) => value is string text ? DateTime.Parse(text, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal) : null;

    /// <summary>A timestamp as exact UTC text (microseconds), independent of the session time zone.</summary>
    private static string Utc(string column) => $"to_char({column} AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"')";

    private sealed record SourceRow(string Kind, int Role, Guid? GroupId, string? GroupName, Guid? GrantId, DateTime? StartAt,
        DateTime? ExpiresAt, DateTime? ActiveFrom, bool Effective);

    private const string EffectiveGrant = """
        (g."StartAt" IS NULL OR g."StartAt" <= statement_timestamp()) AND (g."ExpiresAt" IS NULL OR statement_timestamp() < g."ExpiresAt")
        """;

    // Parameters :page, :space and :own (the locked page's OwnAccess). One row per (account, source); a group grant expands to
    // the group's current members. Page grants: every current row, Effective only inside its validity (as EffectiveResourceGrant).
    private static readonly string SourcesSql = """
        WITH sources AS (
            SELECT u."Id" AS "UserId", 3 AS "Role", 'global' AS "Kind", CAST(NULL AS uuid) AS "GroupId", CAST(NULL AS uuid) AS "GrantId",
                CAST(NULL AS timestamptz) AS "StartAt", CAST(NULL AS timestamptz) AS "ExpiresAt", CAST(NULL AS timestamptz) AS "ActiveFrom",
                true AS "Effective"
            FROM "UserAccount" u WHERE u."IsWikiAdmin"
            UNION ALL
            SELECT h."UserId", 3, 'hubGlobal', NULL, NULL, NULL, h."HubVerifiedUntil", NULL, true FROM "EffectiveWikiAdmin" h
            UNION ALL
            SELECT g."UserId", g."Role", CASE WHEN g."Role" = 3 THEN 'spaceAdmin' ELSE 'space' END, NULL, g."Id", g."StartAt", g."ExpiresAt", NULL, true
            FROM "EffectiveSpaceGrant" g
            WHERE g."SpaceId" = :space AND g."UserId" IS NOT NULL AND (NOT :own OR g."Role" = 3)
            UNION ALL
            SELECT m."UserId", g."Role", CASE WHEN g."Role" = 3 THEN 'spaceAdmin' ELSE 'space' END, g."GroupId", g."Id", g."StartAt", LEAST(g."ExpiresAt", m."VerifiedUntil"), NULL, true
            FROM "EffectiveSpaceGrant" g
            JOIN "EffectiveGroupMember" m ON m."GroupId" = g."GroupId" AND m."ActiveTo" IS NULL
            WHERE g."SpaceId" = :space AND (NOT :own OR g."Role" = 3)
            UNION ALL
            SELECT COALESCE(g."UserId", m."UserId"), g."Role", 'page', g."GroupId", g."Id", g."StartAt", LEAST(g."ExpiresAt", m."VerifiedUntil"), g."ActiveFrom",
        """ + EffectiveGrant + """
            FROM "ResourceGrant" g
            LEFT JOIN "EffectiveGroupMember" m ON m."GroupId" = g."GroupId" AND m."ActiveTo" IS NULL
            WHERE g."PageId" = :page AND g."ActiveTo" IS NULL AND (g."UserId" IS NOT NULL OR m."UserId" IS NOT NULL)
        ), roles AS (
            SELECT "UserId", max("Role") FILTER (WHERE "Effective") AS "Role" FROM sources GROUP BY "UserId"
        ), summary AS (
            SELECT s."UserId", max(s."Role") FILTER (WHERE s."Effective") AS "Role",
                CAST(json_agg(json_build_object('Kind', s."Kind", 'Role', s."Role", 'GroupId', s."GroupId", 'GroupName', ug."Name",
                    'GrantId', s."GrantId", 'StartAt',
        """ + Utc("s.\"StartAt\"") + ", 'ExpiresAt', " + Utc("s.\"ExpiresAt\"") + ", 'ActiveFrom', " + Utc("s.\"ActiveFrom\"") + """
                    , 'Effective', s."Effective") ORDER BY s."Effective" DESC, s."Role" DESC, s."Kind", ug."Name") AS text) AS "Sources"
            FROM sources s LEFT JOIN "UserGroup" ug ON ug."Id" = s."GroupId"
            GROUP BY s."UserId"
        )
        """;

    // Continues SourcesSql: the space-default role of every account regardless of own access and page grants (what "Výchozí
    // prostoru" would give), next to the current role.
    private const string ImpactSql = """
        , defaults AS (
            SELECT g."UserId", g."Role" FROM "EffectiveSpaceGrant" g WHERE g."SpaceId" = :space AND g."UserId" IS NOT NULL
            UNION ALL
            SELECT m."UserId", g."Role" FROM "EffectiveSpaceGrant" g
            JOIN "EffectiveGroupMember" m ON m."GroupId" = g."GroupId" AND m."ActiveTo" IS NULL
            WHERE g."SpaceId" = :space
            UNION ALL
            SELECT u."Id", 3 FROM "UserAccount" u WHERE u."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=u."Id")
        ), default_roles AS (
            SELECT "UserId", max("Role") AS "Role" FROM defaults GROUP BY "UserId"
        ), people AS (
            SELECT u."Id" AS "UserId", r."Role" AS "Current", d."Role" AS "Default"
            FROM roles r FULL JOIN default_roles d ON d."UserId" = r."UserId"
            JOIN "UserAccount" u ON u."Id" = COALESCE(r."UserId", d."UserId") AND u."IsActive"
        )
        """;

    private const string PeopleSql = """
        SELECT u."Id", u."DisplayName", u."Username", (u."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=u."Id")), s."Role", COALESCE(s."Sources", '[]') AS "Sources",
            (SELECT max(e."OccurredAt") FROM "PageAccessEvent" e
                WHERE e."ActorId" = u."Id" AND e."PageId" = :page AND e."Action" = 'page.opened') AS "LastViewedAt",
            GREATEST((SELECT max(r."ActiveFrom") FROM "PageRevision" r WHERE r."PageId" = :page AND r."AuthorId" = u."Id"),
                (SELECT max(c."ReceivedAt") FROM "CollaborationUpdate" c WHERE c."PageId" = :page AND c."UserId" = u."Id")) AS "LastEditedAt"
        FROM "UserAccount" u
        LEFT JOIN summary s ON s."UserId" = u."Id" AND u."IsActive"
        WHERE
        """;
}
