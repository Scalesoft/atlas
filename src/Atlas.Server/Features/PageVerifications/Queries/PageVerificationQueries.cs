using Atlas.Server.Features.Mcp.Domain;
using Atlas.Server.Features.PageVerifications.Domain;
using Atlas.Server.Features.PersonalLibrary;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageVerifications.Queries;

/// <summary>The page's verification for whoever may read the page (<c>GET /api/pages/{id}/verification</c>).</summary>
public sealed record GetPageVerification(Guid PageId);

/// <summary>
/// Stav obsahu (plan §6): the space's published pages with their verification and readership, for its admins and writers.
/// </summary>
/// <param name="Filter">all, due (expiring, expired, unverified), without_steward or unread (no view in the period).</param>
/// <param name="Mine">Only pages whose steward is the caller.</param>
/// <param name="Sort">due_read (default: „Nejdřív čtené a neověřené“), expiry, readers or published.</param>
/// <param name="Days">The period of the readership: 30, 90 (default) or 365 days.</param>
/// <param name="Query">Part of the title.</param>
public sealed record GetContentHealth(Guid SpaceId, string? Filter = null, bool Mine = false, string? Sort = null, int Days = 90,
    string? Query = null, string? Cursor = null, int Limit = 50);

public sealed class PageVerificationQueryHandler(ISession session, PageGate gate, PageVerificationReader reader, IContentAccess access,
    McpPagePaths paths, TimeProvider clock)
{
    public async Task<PageVerificationView> Handle(GetPageVerification query, CancellationToken ct)
    {
        var opened = (await gate.ReadAsync(query.PageId, AccessOperation.ReadContent, ct)).Require();
        var published = opened.Page.PublishedRevisionId is { } revision
            ? await session.QueryOver<PageRevision>().Where(x => x.Id == revision).Select(x => x.Version).SingleOrDefaultAsync<int?>(ct)
            : null;
        return await reader.ReadAsync(opened.Page.Id, published, ct);
    }

    public async Task<ContentHealthList> Handle(GetContentHealth query, CancellationToken ct)
    {
        if (query.Days is not (30 or 90 or 365))
        {
            throw new ApiException(400, "Vyberte období 30, 90 nebo 365 dní.");
        }
        if (query.Limit is < 1 or > 100)
        {
            throw new ApiException(400, "Počet položek musí být 1 až 100.");
        }
        Func<Row, bool> filter = query.Filter switch
        {
            null or "" or "all" => _ => true,
            "due" => row => PageVerificationStates.IsDue(row.State),
            "without_steward" => row => row.State != PageVerificationStates.Off && row.StewardId is null,
            "unread" => row => row.Read.Views == 0,
            _ => throw new ApiException(400, "Neznámý filtr stavu obsahu.")
        };
        var offset = AnalyticsHandler.SpaceCursor(query.Cursor);
        var actor = await AnalyticsHandler.RequireSpaceEditorAsync(access, query.SpaceId, ct);
        var space = (await session.GetAsync<Space>(query.SpaceId, ct))!;
        var now = clock.GetUtcNow().UtcDateTime;
        var (start, end) = AnalyticsHandler.LastDays(query.Days, now);
        var reads = await AnalyticsHandler.SpaceReadsAsync(session, actor.Id, query.SpaceId, start, end, ct);
        var verifications = (await session.ReadRowsAsync("""
                SELECT v."PageId" AS "PageId", s."Id" AS "StewardId", s."DisplayName" AS "StewardName", v."IntervalMonths" AS "IntervalMonths",
                    v."VerifiedUntil" AS "VerifiedUntil", v."VerifiedVersion" AS "VerifiedVersion"
                FROM "PageVerification" v JOIN "Page" p ON p."Id"=v."PageId"
                LEFT JOIN "UserAccount" s ON s."Id"=v."StewardId" AND s."IsActive"
                WHERE p."SpaceId"=:space
                """, new Dictionary<string, object> { ["space"] = query.SpaceId },
            row => (PageId: row.GetGuid(0), StewardId: row.IsDBNull(1) ? (Guid?)null : row.GetGuid(1), StewardName: row.IsDBNull(2) ? null : row.GetString(2),
                Interval: row.IsDBNull(3) ? (short?)null : row.GetInt16(3), Until: row.IsDBNull(4) ? (DateTime?)null : DateTime.SpecifyKind(row.GetDateTime(4), DateTimeKind.Utc),
                Version: row.IsDBNull(5) ? (int?)null : row.GetInt32(5)), ct)).ToDictionary(x => x.PageId);
        var rows = reads.Select(read =>
        {
            var found = verifications.TryGetValue(read.PageId, out var v);
            var state = PageVerificationStates.Of(PageVerificationStates.Interval(found ? v.Interval : null, space.VerificationIntervalMonths, false),
                found ? v.Until : null, now);
            return new Row(read, state, found ? v.StewardId : null, found ? v.StewardName : null, found ? v.Until : null, found ? v.Version : null);
        }).ToList();
        var on = rows.Where(row => row.State != PageVerificationStates.Off).ToList();
        var summary = new ContentHealthSummary(on.Count(row => row.State == PageVerificationStates.Verified),
            on.Count(row => row.State == PageVerificationStates.Expiring),
            on.Count(row => row.State is PageVerificationStates.Expired or PageVerificationStates.Unverified), on.Count(row => row.StewardId is null));
        var text = query.Query?.Trim();
        var shown = rows.Where(filter).Where(row => !query.Mine || row.StewardId == actor.Id)
            .Where(row => string.IsNullOrEmpty(text) || row.Read.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        var ordered = (query.Sort switch
        {
            null or "" or "due_read" => shown.OrderByDescending(row => PageVerificationStates.IsDue(row.State)).ThenByDescending(row => row.Read.Readers)
                .ThenByDescending(row => row.Read.Views),
            // Unverified first: it has no date and is due already.
            "expiry" => shown.OrderBy(row => row.State == PageVerificationStates.Off).ThenBy(row => row.Until ?? DateTime.MinValue),
            "readers" => shown.OrderByDescending(row => row.Read.Readers).ThenByDescending(row => row.Read.Views),
            "published" => shown.OrderByDescending(row => row.Read.LastPublishedAt),
            _ => throw new ApiException(400, "Neznámé řazení stavu obsahu.")
        }).ThenBy(row => row.Read.Title, StringComparer.Ordinal).ThenBy(row => row.Read.PageId).ToList();
        var page = ordered.Skip(offset).Take(query.Limit).ToList();
        var described = page.Count == 0 ? new Dictionary<Guid, string>()
            : await paths.DescribeAsync(actor, page.ToDictionary(row => row.Read.PageId, row => (space.Name, row.Read.Title)), ct);
        return new ContentHealthList(summary, page.Select(row => new ContentHealthItem(row.Read.PageId, row.Read.Title,
                described.GetValueOrDefault(row.Read.PageId) ?? space.Name, row.State, row.StewardId, row.StewardName, row.Until,
                row.VerifiedVersion is { } verified && row.Read.PublishedVersion > verified, row.Read.Readers, row.Read.Views, row.Read.LastViewedAt,
                row.Read.LastPublishedAt, row.Read.PublishedVersion, row.VerifiedVersion)).ToArray(),
            ordered.Count, ordered.Count > offset + query.Limit ? (offset + query.Limit).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    private sealed record Row(AnalyticsHandler.SpaceRead Read, string State, Guid? StewardId, string? StewardName, DateTime? Until, int? VerifiedVersion);
}
