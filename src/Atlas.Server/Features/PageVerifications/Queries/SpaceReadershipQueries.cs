using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageVerifications.Queries;

/// <summary>The readership reads of Stav obsahu (<see cref="PageVerificationQueryHandler"/>) and of the panel's „Čtenost“.</summary>
public static class SpaceReadership
{
    internal static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    /// <summary>One published page of a space with its readership in the period (<see cref="SpaceReadsAsync"/>); the last view is a Prague
    /// day only, so a single reader is not singled out by the time.</summary>
    public sealed record SpaceRead(Guid PageId, Guid? ParentId, string Title, int PublishedVersion, long Views, long Readers, DateOnly? LastViewedAt,
        DateTime? LastPublishedAt);

    /// <summary>
    /// Every published, untrashed, non-template page of the space the actor may read (EffectivePageAccess), with its views, distinct
    /// readers and last view in [start, end) and its last publication: one statement, each page's views counted through
    /// IX_PageView_PageTime. Counts only, never who read. Shared by Stav obsahu (plan §6).
    /// </summary>
    /// <remarks>shortcut: no daily aggregates (plan §5.2): measured by PageVerificationMigrationTests (5 000 pages, 300 ms); add an aggregate table only when a
    /// space of 5 000 pages over 365 days takes more than 300 ms.</remarks>
    public static Task<List<SpaceRead>> SpaceReadsAsync(ISession session, Guid actorId, Guid spaceId, DateTime start, DateTime end, CancellationToken ct)
        => session.ReadRowsAsync(SpaceReadsSql, new Dictionary<string, object> { ["actor"] = actorId, ["space"] = spaceId, ["start"] = start, ["end"] = end },
            row => new SpaceRead(row.GetGuid(0), row.IsDBNull(1) ? null : row.GetGuid(1), row.GetString(2), row.GetInt32(3), row.GetInt64(4),
                row.GetInt64(5), row.IsDBNull(6) ? null : DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Utc(row.GetDateTime(6)), Prague)), row.IsDBNull(7) ? null : Utc(row.GetDateTime(7))), ct);

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

    /// <summary>
    /// Distinct readers of one page over the last <paramref name="days"/> calendar days in Prague (the panel's „Čtenost“): the same period
    /// as <see cref="LastDays"/> gives Stav obsahu, so both show the same number for the page.
    /// </summary>
    public static async Task<long> PageReadersAsync(ISession session, Guid pageId, int days, DateTime now, CancellationToken ct)
    {
        var (start, end) = LastDays(days, now);
        return await session.CreateSQLQuery("""
                SELECT count(DISTINCT v."UserId") AS "Readers" FROM "PageView" v WHERE v."PageId"=:page AND v."ViewedAt">=:start AND v."ViewedAt"<:end
                """).AddScalar("Readers", NHibernate.NHibernateUtil.Int64).SetParameter("page", pageId)
            .SetParameter("start", start, NHibernate.NHibernateUtil.UtcDateTime).SetParameter("end", end, NHibernate.NHibernateUtil.UtcDateTime)
            .UniqueResultAsync<long>(ct);
    }

    /// <summary>Stav obsahu: its admins and everyone who may write in it (plan §6).</summary>
    public static async Task<UserAccount> RequireSpaceEditorAsync(IContentAccess access, Guid spaceId, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(spaceId, ct);
        await access.RequireSpaceAsync(spaceId, SpaceRole.Writer, ct);
        return actor;
    }

    /// <summary>The (start, end) of the last <paramref name="days"/> days ending today, as the period of the other reads.</summary>
    public static (DateTime Start, DateTime End) LastDays(int days, DateTime now)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, Prague));
        return PersonalLibrary.AnalyticsHandler.Period(today.AddDays(1 - days), today);
    }

    public static int SpaceCursor(string? cursor) => cursor is null or ""
        ? 0
        : int.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var offset) && offset <= 100_000
            ? offset
            : throw new ApiException(400, "Neplatný posun seznamu.");

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
