using Atlas.Server.Features.ExternalSharing.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ExternalSharing;

/// <summary>Bounded, untracked projections; no credential material or hidden entity graphs leave the handler.</summary>
public sealed class ShareLinkReader(ISession session)
{
    public async Task<ShareLinksDto> ReadAsync(Guid spaceId, Guid? pageId, Guid? linkId, int offset, int limit, CancellationToken ct)
    {
        if (offset < 0 || offset > 100000 || limit is < 1 or > 100)
            throw new ApiException(400, "Neplatné stránkování odkazů.");
        var sql = """
            SELECT l."Id",l."PageId",r."Title",l."Name",r."Version",u."DisplayName",l."ActiveFrom",l."ExpiresAt",l."ActiveTo",
              CASE WHEN l."ActiveTo" IS NOT NULL THEN 'revoked'
                   WHEN l."ExpiresAt"<=:now THEN 'expired'
                   WHEN NOT p."Enabled" OR p."ActiveTo" IS NOT NULL OR page."IsTrashed" OR page."SpaceId"<>l."SpaceId"
                     OR page."PublishedRevisionId" IS NULL OR NOT u."IsActive" OR u."SecurityStamp"<>l."CreatorSecurityStamp" THEN 'unavailable'
                   ELSE 'active' END,
              (SELECT e."OccurredAt" FROM "PageAccessEvent" e WHERE e."ShareLinkId"=l."Id"
                AND e."Action"='share.opened' ORDER BY e."OccurredAt" DESC LIMIT 1),
              l."FollowsLive"
            FROM "PageShareLink" l
            JOIN "PageRevision" r ON r."Id"=l."RevisionId" AND r."PageId"=l."PageId"
            JOIN "Page" page ON page."Id"=l."PageId"
            JOIN "UserAccount" u ON u."Id"=l."CreatedById"
            JOIN "SpaceExternalSharingPolicy" p ON p."Id"=l."PolicyId"
            WHERE l."SpaceId"=:space
            """;
        if (pageId is not null) sql += " AND l.\"PageId\"=:page";
        if (linkId is not null) sql += " AND l.\"Id\"=:link";
        sql += " ORDER BY l.\"ActiveFrom\" DESC,l.\"Id\" DESC";
        var query = session.CreateSQLQuery(sql).SetParameter("space", spaceId).SetParameter("now", DateTime.UtcNow)
            .SetFirstResult(offset).SetMaxResults(limit + 1);
        if (pageId is { } page) query.SetParameter("page", page);
        if (linkId is { } link) query.SetParameter("link", link);
        var rows = await query.ListAsync<object[]>(ct);
        return new ShareLinksDto(rows.Take(limit).Select(row => new ShareLinkDto((Guid)row[0], (Guid)row[1],
            (string)row[2], (string)row[3], (int)row[4], (string)row[5], (DateTime)row[6], (DateTime)row[7],
            row[8] is DateTime revoked ? revoked : null, (string)row[9], row[10] is DateTime opened ? opened : null, (bool)row[11])).ToList(), rows.Count > limit);
    }
}
