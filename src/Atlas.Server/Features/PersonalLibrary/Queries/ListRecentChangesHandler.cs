using System.Globalization;
using Atlas.Server.Features.PageHistory.Domain;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.Relations.Persistence;
using Atlas.Server.Platform.Persistence;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalLibrary.Queries;

/// <summary>
/// Co se změnilo (home dashboard, 4.0): the pages changed within <see cref="Window"/> that the caller may read, newest change first, one
/// row per page. One statement: the pages are decided by <see cref="RelationNodes"/> (EffectivePageAccess and the list rule of
/// PageListing, live page and space, and on MCP or for the assistant no space out of AI, G5), as the inbox decides them; a change is a
/// version of the page history (its group's newest revision), and one of unpublished work only for a writer of the page, as in the
/// library lists. Keyset paging by (newest change, page).
/// </summary>
public sealed class ListRecentChangesHandler(ISession session, IContentAccess access, RelationNodes nodes)
{
    /// <summary>How far back the dashboard looks: the query reads only revisions of this window (IX_PageRevision_Activity).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    private const string Candidates = """SELECT DISTINCT r."PageId" FROM "PageRevision" r WHERE r."ActiveFrom">=:since""";

    private static readonly string Sql = $$"""
        WITH node AS ({{RelationNodes.Over(Candidates)}}),
        change AS MATERIALIZED (
          SELECT r."Id",r."PageId",r."Version",r."ActiveFrom",r."AuthorId",r."Channel",r."ApiTokenId",r."AiAssisted"
          FROM "PageRevision" r
          JOIN node v ON v."Id"=r."PageId" AND v.live AND v.readable
          JOIN "Page" p ON p."Id"=r."PageId" AND NOT p."IsTemplate"
          LEFT JOIN "PageRevision" pub ON pub."Id"=p."PublishedRevisionId"
          WHERE r."ActiveFrom">=:since AND r."SameAsRevisionId" IS NULL
            AND NOT EXISTS (SELECT 1 FROM "PageRevision" n WHERE n."PageId"=r."PageId" AND n."GroupId"=r."GroupId" AND n."Version">r."Version")
            AND (v.can_edit OR r."Version"<=pub."Version")
            AND (NOT :aiOnly OR r."Channel"=ANY(CAST(string_to_array(:aiChannels, ',') AS text[])) OR r."AiAssisted")
            AND (NOT :mineOnly OR p."OwnerId"=:actor
              OR EXISTS (SELECT 1 FROM "PageContribution" pc WHERE pc."UserId"=:actor AND pc."PageId"=p."Id")
              OR EXISTS (SELECT 1 FROM "PageFavorite" f WHERE f."UserId"=:actor AND f."PageId"=p."Id" AND f."ActiveTo" IS NULL))
        ), latest AS (
          SELECT "PageId",max("ActiveFrom") AS at,count(*) AS versions FROM change GROUP BY "PageId"
        )
        SELECT g."PageId",v.title,v."SpaceId",v."SpaceName",c."Id",c."Version",c."ActiveFrom",c."AuthorId",u."DisplayName",c."Channel",
          c."ApiTokenId",c."AiAssisted",CAST(g.versions AS int),v.can_edit
        FROM latest g
        JOIN LATERAL (SELECT * FROM change c WHERE c."PageId"=g."PageId" ORDER BY c."ActiveFrom" DESC,c."Version" DESC LIMIT 1) c ON true
        JOIN node v ON v."Id"=g."PageId"
        JOIN "UserAccount" u ON u."Id"=c."AuthorId"
        WHERE :first OR (g.at,g."PageId")<(:beforeAt,:beforePage)
        ORDER BY g.at DESC,g."PageId" DESC LIMIT :take
        """;

    public async Task<RecentChangeList> Handle(ListRecentChanges query, CancellationToken ct)
    {
        if (query.Filter is not ("all" or "ai" or "mine"))
        {
            throw new ApiException(400, "Neznámý filtr změn.");
        }
        if (query.Limit is < 1 or > 50)
        {
            throw new ApiException(400, "Počet položek musí být 1–50.");
        }
        var before = Cursor(query.Cursor);
        var actor = await access.LockActorAsync(null, ct);
        var parameters = nodes.Parameters(actor, null, [], [PagesAtlasModule.ContentType]);
        parameters["since"] = DateTime.UtcNow - Window;
        parameters["aiOnly"] = query.Filter == "ai";
        parameters["aiChannels"] = string.Join(',', Pages.Domain.RevisionVersions.AiChannels);
        parameters["mineOnly"] = query.Filter == "mine";
        parameters["first"] = before is null;
        parameters["beforeAt"] = before?.At ?? DateTime.UnixEpoch;
        parameters["beforePage"] = before?.PageId ?? Guid.Empty;
        parameters["take"] = query.Limit + 1;
        // A wiki's worth of access decisions inflates the plan's cost estimate, and JIT would compile for longer than it runs (RelationNodes).
        var rows = await session.ReadRowsAsync(Sql, parameters, row => new
        {
            Change = new RecentChange(row.GetGuid(0), row.GetString(1), row.IsDBNull(2) ? null : row.GetGuid(2), row.IsDBNull(3) ? null : row.GetString(3),
                row.GetGuid(4), row.GetInt32(5), Utc(row.GetDateTime(6)), row.GetGuid(7), row.GetString(8), ContentChannels.ForRead(row.GetString(9)),
                Pages.Domain.RevisionVersions.IsAi(row.GetString(9), row.GetBoolean(11)), null, row.GetInt32(12) - 1, row.GetBoolean(13)),
            Token = row.IsDBNull(10) ? (Guid?)null : row.GetGuid(10)
        }, ct, "SELECT set_config('jit','off',true)");
        var page = rows.Take(query.Limit).ToArray();
        var tokens = await ApiTokenVisibility.VisibleAsync(session, actor, page.Select(row => row.Token), ct);
        var items = page.Select(row => row.Change with { ApiToken = tokens.Find(row.Token) }).ToArray();
        return new RecentChangeList(items, rows.Count > query.Limit ? $"{items[^1].At.Ticks}_{items[^1].PageId:N}" : null);
    }

    private static (DateTime At, Guid PageId)? Cursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }
        var parts = cursor.Split('_');
        return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTime.MaxValue.Ticks && Guid.TryParseExact(parts[1], "N", out var pageId)
            ? (new DateTime(ticks, DateTimeKind.Utc), pageId)
            : throw new ApiException(400, "Neplatný kurzor seznamu změn.");
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
