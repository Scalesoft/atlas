using Atlas.Server.Features.PageVerifications.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageVerifications;

/// <summary>The page's <see cref="PageVerificationView"/>, read by a caller that has already decided the page may be read.</summary>
public sealed class PageVerificationReader(ISession session, TimeProvider clock)
{
    /// <summary>The window of the panel's „Čtenost“ (plan §5.1).</summary>
    public const int ReaderDays = 90;

    public async Task<PageVerificationView> ReadAsync(Guid pageId, CancellationToken ct)
    {
        // One statement (the page view's SQL budget): the space's interval, the published version, the row and both names. A steward not in effect (deactivated, or unable to read the page) reads as none.
        var rows = await session.ReadRowsAsync($"""
                SELECT p."IsTemplate" AS "IsTemplate", s."VerificationIntervalMonths" AS "SpaceInterval", v."IntervalMonths" AS "Interval",
                    st."Id" AS "StewardId", st."DisplayName" AS "StewardName", v."VerifiedAt" AS "VerifiedAt", v."VerifiedUntil" AS "VerifiedUntil",
                    v."VerifiedVersion" AS "VerifiedVersion", vb."Id" AS "VerifiedById", vb."DisplayName" AS "VerifiedByName", v."VerifiedChannel" AS "Channel",
                    pub."Version" AS "PublishedVersion"
                FROM "Page" p JOIN "Space" s ON s."Id"=p."SpaceId"
                LEFT JOIN "PageRevision" pub ON pub."Id"=p."PublishedRevisionId"
                LEFT JOIN "PageVerification" v ON v."PageId"=p."Id"
                {PageVerificationStates.StewardJoinSql}
                LEFT JOIN "UserAccount" vb ON vb."Id"=v."VerifiedById"
                WHERE p."Id"=:page
                """, new Dictionary<string, object> { ["page"] = pageId }, read => new
            {
                IsTemplate = read.GetBoolean(0), SpaceInterval = Short(read, 1), Interval = Short(read, 2), StewardId = Id(read, 3), StewardName = Text(read, 4),
                VerifiedAt = Time(read, 5), VerifiedUntil = Time(read, 6), VerifiedVersion = read.IsDBNull(7) ? (int?)null : read.GetInt32(7),
                VerifiedById = Id(read, 8), VerifiedByName = Text(read, 9), Channel = Text(read, 10), PublishedVersion = read.IsDBNull(11) ? (int?)null : read.GetInt32(11)
            }, ct);
        var x = rows.SingleOrDefault() ?? throw new ApiException(404, "Stránka nebyla nalezena.");
        var interval = PageVerificationStates.Interval(x.Interval, x.SpaceInterval, x.IsTemplate);
        return new PageVerificationView(PageVerificationStates.Of(interval, x.VerifiedUntil, clock.GetUtcNow().UtcDateTime), x.StewardId,
            x.StewardName, x.VerifiedAt, x.VerifiedUntil, x.VerifiedVersion, x.VerifiedVersion is { } verified && x.PublishedVersion > verified,
            x.VerifiedById, x.VerifiedByName, x.Channel is { } channel && RevisionVersions.AiChannels.Contains(channel), x.Interval,
            x.SpaceInterval, x.PublishedVersion);
    }

    /// <summary>The view with the panel's „Čtenost“ while verification is on (GET and PUT …/verification), so a change needs no other read.</summary>
    public async Task<PageVerificationView> ReadWithReadersAsync(Guid pageId, CancellationToken ct)
    {
        var view = await ReadAsync(pageId, ct);
        return view.State == PageVerificationStates.Off ? view : view with { Readers = await ReadersAsync(pageId, ct) };
    }

    /// <summary>
    /// Distinct readers of one page over the last <see cref="ReaderDays"/> calendar days in Prague (the panel's „Čtenost“): the same period
    /// as <see cref="Queries.SpaceReadership.LastDays"/> gives Stav obsahu, so both show the same number for the page.
    /// </summary>
    public async Task<long> ReadersAsync(Guid pageId, CancellationToken ct)
    {
        var (start, end) = Queries.SpaceReadership.LastDays(ReaderDays, clock.GetUtcNow().UtcDateTime);
        return await session.CreateSQLQuery("""
                SELECT count(DISTINCT v."UserId") AS "Readers" FROM "PageView" v WHERE v."PageId"=:page AND v."ViewedAt">=:start AND v."ViewedAt"<:end
                """).AddScalar("Readers", NHibernate.NHibernateUtil.Int64).SetParameter("page", pageId)
            .SetParameter("start", start, NHibernate.NHibernateUtil.UtcDateTime).SetParameter("end", end, NHibernate.NHibernateUtil.UtcDateTime)
            .UniqueResultAsync<long>(ct);
    }

    private static short? Short(System.Data.Common.DbDataReader row, int column) => row.IsDBNull(column) ? null : row.GetInt16(column);

    private static Guid? Id(System.Data.Common.DbDataReader row, int column) => row.IsDBNull(column) ? null : row.GetGuid(column);

    private static string? Text(System.Data.Common.DbDataReader row, int column) => row.IsDBNull(column) ? null : row.GetString(column);

    private static DateTime? Time(System.Data.Common.DbDataReader row, int column)
        => row.IsDBNull(column) ? null : DateTime.SpecifyKind(row.GetDateTime(column), DateTimeKind.Utc);

    /// <summary>The state of each listed page whose verification is on (search results, plan §3.3), in one statement; the caller has
    /// already decided the pages may be read.</summary>
    public Task<Dictionary<Guid, string>> StatesAsync(List<Guid> pageIds, CancellationToken ct)
        => ReadStatesAsync(Scalars(session.CreateSQLQuery(StatesSql("""p."Id" IN (:pages)"""))).SetParameterList("pages", pageIds.Distinct().ToList()), ct);

    /// <summary>The same for every page of a space (the space tree's link chips), in one statement however large the space.</summary>
    public Task<Dictionary<Guid, string>> StatesInSpaceAsync(Guid spaceId, CancellationToken ct)
        => ReadStatesAsync(Scalars(session.CreateSQLQuery(StatesSql("""p."SpaceId"=:space AND NOT p."IsTrashed" """))).SetParameter("space", spaceId), ct);

    private static string StatesSql(string pages) => $"""
        SELECT p."Id" AS "PageId", {PageVerificationStates.IntervalSql} AS "Interval", v."VerifiedUntil" AS "VerifiedUntil"
        FROM "Page" p JOIN "Space" s ON s."Id"=p."SpaceId" LEFT JOIN "PageVerification" v ON v."PageId"=p."Id"
        WHERE {pages} AND {PageVerificationStates.IntervalSql}>0
        """;

    private static NHibernate.ISQLQuery Scalars(NHibernate.ISQLQuery query) => query.AddScalar("PageId", NHibernate.NHibernateUtil.Guid)
        .AddScalar("Interval", NHibernate.NHibernateUtil.Int16).AddScalar("VerifiedUntil", NHibernate.NHibernateUtil.UtcDateTime);

    private async Task<Dictionary<Guid, string>> ReadStatesAsync(NHibernate.IQuery query, CancellationToken ct)
    {
        var rows = await query.ListAsync<object[]>(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        return rows.Select(row => (Id: (Guid)row[0], State: PageVerificationStates.Of((short?)row[1], (DateTime?)row[2], now)))
            .Where(x => x.State != PageVerificationStates.Off).ToDictionary(x => x.Id, x => x.State);
    }
}
