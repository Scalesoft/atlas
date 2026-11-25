using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageVerifications.Queries;

/// <summary>5.2 (plan §5.2): the readership of the space's published pages the caller may read, over the period, for its editors and
/// admins. Filter all or unread (no view in the period); cursor: NextCursor of the previous page.</summary>
public sealed record GetSpaceReadership(Guid SpaceId, DateOnly From, DateOnly To, string? Filter = null, string? Cursor = null);

/// <summary>The space's readership and the reads it shares with Stav obsahu (<see cref="PageVerificationQueryHandler"/>).</summary>
public sealed class SpaceReadershipHandler(ISession session, IContentAccess access)
{
    private const int PageSize = 50;
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    public async Task<SpaceReadership> Handle(GetSpaceReadership query, CancellationToken ct)
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
        return new(shown.Skip(offset).Take(PageSize).Select(row => new SpaceReadershipItem(
                row.PageId, row.Title, row.Views, row.Readers, row.LastViewedAt, row.LastPublishedAt)).ToArray(),
            shown.Count > offset + PageSize ? (offset + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    /// <summary>One published page of a space with its readership in the period (<see cref="SpaceReadsAsync"/>).</summary>
    public sealed record SpaceRead(Guid PageId, Guid? ParentId, string Title, int PublishedVersion, long Views, long Readers, DateTime? LastViewedAt,
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
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, Prague));
        return Period(today.AddDays(1 - days), today);
    }

    public static int SpaceCursor(string? cursor) => cursor is null or ""
        ? 0
        : int.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var offset) && offset <= 100_000
            ? offset
            : throw new ApiException(400, "Neplatný posun seznamu.");

    // The same days in Prague as the personal analytics' periods (PersonalLibrary.AnalyticsHandler), up to 366 of them.
    private static (DateTime Start, DateTime End) Period(DateOnly from, DateOnly to)
    {
        if (from.Year < 1970 || to.Year >= 9999 || to < from || to.DayNumber - from.DayNumber > 365)
            throw new ApiException(400, "Vyberte období od 1 do 366 dní.");
        return (TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), Prague),
            TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), Prague));
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
