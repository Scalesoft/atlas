using NHibernate;
using Atlas.Server.Features.PersonalLibrary.Domain;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalLibrary;

public sealed class AnalyticsHandler(ISession session, PersonalPageGuard guard, VaultGrantSnapshots vaultGrants, IContentAccess access)
{
    private const int PageSize = 50;
    private const string Zone = "Europe/Prague";
    private static readonly TimeZoneInfo TimeZone = TimeZoneInfo.FindSystemTimeZoneById(Zone);
    // Page roles from EffectivePageAccess (WP3 S3), including group grants, item grants and wiki administrators.
    private const string VisiblePages = """
        WITH visible AS (
            SELECT p."Id", m."Role",
                CASE WHEN m."Role">=2 THEN w."Title" ELSE pub."Title" END AS "Title"
            FROM "EffectivePageAccess" m
            JOIN "Page" p ON p."Id"=m."PageId"
            JOIN "PageRevision" w ON w."PageId"=p."Id" AND w."ActiveTo" IS NULL
            LEFT JOIN "PageRevision" pub ON pub."Id"=p."PublishedRevisionId"
             WHERE m."UserId"=:actor AND NOT p."IsTrashed" AND NOT p."IsTemplate"
              AND (p."PublishedRevisionId" IS NOT NULL OR (m."Role">=2 AND (p."OwnerId"=:actor OR EXISTS (
                  SELECT 1 FROM "UserAccount" u WHERE u."Id"=:actor AND (u."IsWikiAdmin" OR EXISTS (
                      SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=u."Id"))))))
        )
        """;
    private const string RevisionFilter = """
        FROM "PageRevision" r JOIN visible p ON p."Id"=r."PageId" AND p."Role">=2
        WHERE r."ActiveFrom">=:start AND r."ActiveFrom"<:end
        AND (:all OR r."AuthorId"=:actor)
        """;

    public async Task<ActivitySummary> Handle(GetActivity query, CancellationToken ct)
    {
        var (start, end) = Period(query.From, query.To);
        var all = Scope(query.Scope);
        var actor = await guard.LockIdentityAsync(null, false, ct);
        var totals = await Revisions("""SELECT count(*) AS revisions, count(DISTINCT r."PageId") AS pages """ + RevisionFilter,
            actor, start, end, all).UniqueResultAsync<object[]>(ct);
        var days = await Revisions("""
            SELECT to_char(timezone('Europe/Prague',r."ActiveFrom"),'YYYY-MM-DD'), count(*)
            """ + RevisionFilter + " GROUP BY 1 ORDER BY 1", actor, start, end, all).ListAsync<object[]>(ct);
        var authors = await Revisions("""
            SELECT r."AuthorId", u."DisplayName", count(*) AS revisions, count(DISTINCT r."PageId") AS pages
            FROM "PageRevision" r JOIN visible p ON p."Id"=r."PageId" AND p."Role">=2
            JOIN "UserAccount" u ON u."Id"=r."AuthorId"
            WHERE r."ActiveFrom">=:start AND r."ActiveFrom"<:end AND (:all OR r."AuthorId"=:actor)
            GROUP BY r."AuthorId", u."DisplayName" ORDER BY count(*) DESC, u."DisplayName", r."AuthorId"
            """, actor, start, end, all).SetMaxResults(20).ListAsync<object[]>(ct);
        return new ActivitySummary((long)totals[0], (long)totals[1],
            days.Select(row => new ActivityDay((string)row[0], (long)row[1])).ToArray(),
            authors.Select(row => new ActivityAuthor((Guid)row[0], (string)row[1], (long)row[2], (long)row[3])).ToArray());
    }

    public async Task<ActivityChanges> Handle(GetActivityChanges query, CancellationToken ct)
    {
        Offset(query.Offset);
        var (start, end) = Period(query.Day, query.Day);
        var all = Scope(query.Scope);
        var actor = await guard.LockIdentityAsync(null, false, ct);
        var rows = await Revisions("""
            SELECT r."PageId", r."Title", r."AuthorId", u."DisplayName", r."ActiveFrom", r."Version"
            FROM "PageRevision" r JOIN visible p ON p."Id"=r."PageId" AND p."Role">=2
            JOIN "UserAccount" u ON u."Id"=r."AuthorId"
            WHERE r."ActiveFrom">=:start AND r."ActiveFrom"<:end AND (:all OR r."AuthorId"=:actor)
            ORDER BY r."ActiveFrom" DESC, r."Id"
            """, actor, start, end, all).SetFirstResult(query.Offset).SetMaxResults(PageSize + 1).ListAsync<object[]>(ct);
        return new ActivityChanges(rows.Take(PageSize).Select(row => new ActivityChange(
            (Guid)row[0], (string)row[1], (Guid)row[2], (string)row[3], Utc(row[4]), (int)row[5])).ToArray(), rows.Count > PageSize);
    }

    public async Task<PageReadership> Handle(GetPageReadership query, CancellationToken ct)
    {
        Offset(query.Offset);
        var (start, end) = Period(query.From, query.To);
        var actor = await guard.RequirePageAsync(query.PageId, false, ct);
        var vaults = await ManagedReleaseVaultsAsync(actor, query.PageId, start, end, ct);
        var totals = await Readership("""
            SELECT (SELECT count(*) FROM "PageView" v WHERE v."PageId"=:page AND v."ViewedAt">=:start AND v."ViewedAt"<:end) AS views,
                (SELECT count(DISTINCT v."UserId") FROM "PageView" v WHERE v."PageId"=:page AND v."ViewedAt">=:start AND v."ViewedAt"<:end) AS readers,
                (SELECT count(*) FROM released) AS secrets
            """, query.PageId, start, end, vaults).UniqueResultAsync<object[]>(ct);
        var days = await Readership("""
            SELECT d.day, sum(d.views) AS views, sum(d.secrets) AS secrets FROM (
                SELECT to_char(timezone('Europe/Prague',v."ViewedAt"),'YYYY-MM-DD') AS day, 1 AS views, 0 AS secrets
                FROM "PageView" v WHERE v."PageId"=:page AND v."ViewedAt">=:start AND v."ViewedAt"<:end
                UNION ALL
                SELECT to_char(timezone('Europe/Prague',r."At"),'YYYY-MM-DD'), 0, 1 FROM released r
            ) d GROUP BY d.day ORDER BY d.day
            """, query.PageId, start, end, vaults).ListAsync<object[]>(ct);
        var rows = await Readership("""
            SELECT x.kind, x.user_id, u."DisplayName", x.at, x.title, x.fields, x.operation FROM (
                SELECT 'view' AS kind, v."UserId" AS user_id, v."ViewedAt" AS at, CAST(NULL AS text) AS title, CAST(NULL AS text) AS fields,
                    CAST(NULL AS text) AS operation, CAST(v."Id" AS text) AS tie
                FROM "PageView" v WHERE v."PageId"=:page AND v."ViewedAt">=:start AND v."ViewedAt"<:end
                UNION ALL
                SELECT 'secret', r."ActorId", r."At", CAST(s."Title" AS text), r."Fields", CAST(r."Operation" AS text), CAST(r."Id" AS text)
                FROM released r JOIN "VaultSecret" s ON s."Id"=r."SecretId"
            ) x JOIN "UserAccount" u ON u."Id"=x.user_id
            ORDER BY x.at DESC, x.kind, x.tie
            """, query.PageId, start, end, vaults).SetFirstResult(query.Offset).SetMaxResults(PageSize + 1).ListAsync<object[]>(ct);
        return new PageReadership((long)totals[0], (long)totals[1], (long)totals[2],
            days.Select(row => new ReadershipDay((string)row[0], Convert.ToInt64(row[1]), Convert.ToInt64(row[2]))).ToArray(),
            rows.Take(PageSize).Select(row => (string)row[0] == "secret"
                ? new ReadershipItem("secret", (Guid)row[1], (string)row[2], Utc(row[3]), (string)row[4], ((string)row[5]).Split(','), (string)row[6])
                : new ReadershipItem("view", (Guid)row[1], (string)row[2], Utc(row[3]))).ToArray(),
            rows.Count > PageSize);
    }

    /// <summary>
    /// The vaults with a secret released on the page in the period that the caller manages (Spravuje, as for the vault audit). Only
    /// their releases appear in Čtenost stránky; anyone else who reads the page sees the views alone.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ManagedReleaseVaultsAsync(UserAccount actor, Guid pageId, DateTime start, DateTime end, CancellationToken ct)
    {
        var ids = await session.CreateSQLQuery("""
            SELECT DISTINCT e."VaultId" FROM "SecretReleaseEvent" e
            WHERE e."PageId"=:page AND e."Kind"='release' AND e."Result"='released' AND e."OccurredAt">=:start AND e."OccurredAt"<:end
            """).SetParameter("page", pageId).SetParameter("start", start).SetParameter("end", end).ListAsync<Guid>(ct);
        if (ids.Count == 0)
        {
            return [];
        }
        var candidates = await session.QueryOver<Vault>().WhereRestrictionOn(v => v.Id).IsIn(ids.ToArray()).ListAsync(ct);
        var snapshot = await vaultGrants.ForVaultsAsync(actor, candidates.ToArray(), ct);
        var now = DateTime.UtcNow;
        return [.. candidates.Where(v => snapshot.AuthorizeVault(v, VaultAccessLevel.Manage, now).Allowed).Select(v => v.Id)];
    }

    // One entry per release request: the audit keeps a row per field, so the fields of one request are folded together in registry order.
    private IQuery Readership(string sql, Guid page, DateTime start, DateTime end, IReadOnlyList<Guid> vaults) =>
        session.CreateSQLQuery("""
            WITH released AS (
                SELECT min(e."Id") AS "Id", e."ActorId", min(e."OccurredAt") AS "At", e."SecretId", e."Operation",
                    string_agg(e."Field", ',' ORDER BY array_position(ARRAY['userName','password','url','notes'], CAST(e."Field" AS text))) AS "Fields"
                FROM "SecretReleaseEvent" e
                WHERE e."PageId"=:page AND e."Kind"='release' AND e."Result"='released' AND e."OccurredAt">=:start AND e."OccurredAt"<:end
                    AND e."VaultId" = ANY(CAST(string_to_array(:vaults, ',') AS uuid[]))
                GROUP BY COALESCE(e."RequestId", CAST(e."Id" AS text)), e."ActorId", e."SecretId", e."Operation"
            )
            """ + sql).SetParameter("page", page).SetParameter("start", start).SetParameter("end", end)
            .SetParameter("vaults", string.Join(",", vaults), NHibernateUtil.String);

    public async Task<PopularPages> Handle(GetPopularPages query, CancellationToken ct)
    {
        var (start, end) = Period(query.From, query.To);
        var actor = await guard.LockIdentityAsync(null, false, ct);
        var rows = await Visible("""
            SELECT p."Id", p."Title", count(*) AS views, count(DISTINCT v."UserId") AS readers
            FROM "PageView" v JOIN visible p ON p."Id"=v."PageId"
            WHERE v."ViewedAt">=:start AND v."ViewedAt"<:end
            GROUP BY p."Id", p."Title" ORDER BY count(*) DESC, p."Title", p."Id"
            """, actor, start, end).SetMaxResults(20).ListAsync<object[]>(ct);
        return new PopularPages(rows.Select(row => new PopularPage((Guid)row[0], (string)row[1], (long)row[2], (long)row[3])).ToArray());
    }

    public async Task<Atlas.Server.Features.PageVerifications.SpaceReadership> Handle(GetSpaceReadership query, CancellationToken ct)
    {
        var (start, end) = Period(query.From, query.To);
        var offset = SpaceCursor(query.Cursor);
        var actor = await RequireSpaceEditorAsync(access, query.SpaceId, ct);
        var rows = await SpaceReadsAsync(session, actor.Id, query.SpaceId, start, end, ct);
        var shown = (query.Filter switch
        {
            null or "" or "all" => rows,
            "unread" => rows.Where(row => row.Views == 0).ToList(),
            _ => throw new ApiException(400, "Neznámý filtr čtenosti.")
        }).OrderByDescending(row => row.Readers).ThenByDescending(row => row.Views).ThenBy(row => row.Title, StringComparer.Ordinal)
            .ThenBy(row => row.PageId).ToList();
        return new(shown.Skip(offset).Take(PageSize).Select(row => new Atlas.Server.Features.PageVerifications.SpaceReadershipItem(
                row.PageId, row.Title, row.Views, row.Readers, row.LastViewedAt, row.LastPublishedAt)).ToArray(),
            shown.Count > offset + PageSize ? (offset + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    /// <summary>One published page of a space with its readership in the period (<see cref="SpaceReadsAsync"/>).</summary>
    public sealed record SpaceRead(Guid PageId, Guid? ParentId, string Title, int PublishedVersion, long Views, long Readers, DateTime? LastViewedAt,
        DateTime? LastPublishedAt);

    /// <summary>
    /// Every published, untrashed, non-template page of the space the actor may read (EffectivePageAccess, as <see cref="VisiblePages"/>),
    /// with its views, distinct readers and last view in [start, end) and its last publication: one statement, each page's views counted
    /// through IX_PageView_PageTime. Counts only, never who read. Shared by Stav obsahu (plan §6).
    /// </summary>
    /// <remarks>shortcut: no daily aggregates (plan §5.2): measured by PageVerificationMigrationTests (5 000 pages, 300 ms); add an aggregate table only when a
    /// space of 5 000 pages over 365 days takes more than 300 ms.</remarks>
    public static Task<List<SpaceRead>> SpaceReadsAsync(ISession session, Guid actorId, Guid spaceId, DateTime start, DateTime end, CancellationToken ct)
        => session.ReadRowsAsync(SpaceReadsSql, new Dictionary<string, object> { ["actor"] = actorId, ["space"] = spaceId, ["start"] = start, ["end"] = end },
            row => new SpaceRead(row.GetGuid(0), row.IsDBNull(1) ? null : row.GetGuid(1), row.GetString(2), row.GetInt32(3), row.GetInt64(4),
                row.GetInt64(5), row.IsDBNull(6) ? null : Utc(row.GetDateTime(6)), row.IsDBNull(7) ? null : Utc(row.GetDateTime(7))), ct);

    /// <summary>The statement of <see cref="SpaceReadsAsync"/> (:actor, :space, :start, :end); public for its budget test.</summary>
    public const string SpaceReadsSql = """
            WITH visible AS MATERIALIZED (
                SELECT p."Id", p."ParentId", pub."Title", pub."Version"
                FROM "EffectivePageAccess" m
                JOIN "Page" p ON p."Id"=m."PageId"
                JOIN "PageRevision" pub ON pub."Id"=p."PublishedRevisionId"
                WHERE m."UserId"=:actor AND m."SpaceId"=:space AND m."Role">=1 AND p."SpaceId"=:space AND NOT p."IsTrashed" AND NOT p."IsTemplate"
            )
            SELECT v."Id" AS "PageId", v."ParentId" AS "ParentId", v."Title" AS "Title", v."Version" AS "Version",
                r."Views" AS "Views", r."Readers" AS "Readers", r."LastViewedAt" AS "LastViewedAt", pp."ActiveFrom" AS "LastPublishedAt"
            FROM visible v
            CROSS JOIN LATERAL (
                SELECT count(*) AS "Views", count(DISTINCT pv."UserId") AS "Readers", max(pv."ViewedAt") AS "LastViewedAt"
                FROM "PageView" pv WHERE pv."PageId"=v."Id" AND pv."ViewedAt">=:start AND pv."ViewedAt"<:end
            ) r
            LEFT JOIN "PagePublication" pp ON pp."PageId"=v."Id" AND pp."ActiveTo" IS NULL
            """;

    /// <summary>Stav obsahu and the space's readership: its admins and everyone who may write in it (plan §6).</summary>
    public static async Task<UserAccount> RequireSpaceEditorAsync(IContentAccess access, Guid spaceId, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(spaceId, ct);
        await access.RequireSpaceAsync(spaceId, SpaceRole.Writer, ct);
        return actor;
    }

    /// <summary>The (start, end) of the last <paramref name="days"/> days ending today, as the period of the other reads.</summary>
    public static (DateTime Start, DateTime End) LastDays(int days, DateTime now)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, TimeZone));
        return Period(today.AddDays(1 - days), today);
    }

    public static int SpaceCursor(string? cursor) => cursor is null or ""
        ? 0
        : int.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var offset) && offset <= 100_000
            ? offset
            : throw new ApiException(400, "Neplatný posun seznamu.");

    private IQuery Visible(string sql, UserAccount actor, DateTime start, DateTime end) =>
        session.CreateSQLQuery(VisiblePages + sql).SetParameter("actor", actor.Id)
            .SetParameter("start", start).SetParameter("end", end);

    private IQuery Revisions(string sql, UserAccount actor, DateTime start, DateTime end, bool all) =>
        Visible(sql, actor, start, end).SetParameter("all", all);

    private static DateTime Utc(object value) => DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc);

    private static (DateTime Start, DateTime End) Period(DateOnly from, DateOnly to)
    {
        if (from.Year < 1970 || to.Year >= 9999 || to < from || to.DayNumber - from.DayNumber > 365)
            throw new ApiException(400, "Vyberte období od 1 do 366 dní.");
        return (TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), TimeZone),
            TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeZone));
    }

    private static bool Scope(string scope) => scope switch
    {
        "mine" => false,
        "wiki" => true,
        _ => throw new ApiException(400, "Neznámý rozsah aktivity.")
    };

    private static void Offset(int offset)
    {
        if (offset < 0)
            throw new ApiException(400, "Neplatný posun seznamu.");
    }
}
