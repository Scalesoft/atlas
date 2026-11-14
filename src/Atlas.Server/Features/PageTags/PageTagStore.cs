using NHibernate;
using Atlas.Server.Features.PageTags.Domain;
using Atlas.Server.Features.Search.Contracts;
using ISession = NHibernate.ISession;
using Atlas.Server.Features.PageTags.Queries;

namespace Atlas.Server.Features.PageTags;

/// <summary>Bounded metadata queries; visibility is applied in SQL before any result limit.</summary>
public sealed class PageTagStore(ISession session)
{
    // Page roles from EffectivePageAccess (WP3 S3), filtered by the actor before the join so the view uses its UserId
    // indexes; the same draft rule as PageListing and PersonalPageGuard. No document bodies enter this projection.
    private static readonly string AccessiblePages = $$"""
        WITH {{AccessSql.ActorCte}}, accessible AS (
          SELECT p."Id",p."SpaceId",s."Name" AS "SpaceName",p."PublishedRevisionId",
            working."Id" AS "WorkingRevisionId",working."Title" AS "WorkingTitle",
            working."ActiveFrom" AS "WorkingAt",published."Title" AS "PublishedTitle",
            publication."ActiveFrom" AS "PublishedAt",permission."Role" AS role
          FROM "EffectivePageAccess" permission
          JOIN "Page" p ON p."Id"=permission."PageId" JOIN "Space" s ON s."Id"=p."SpaceId"
          JOIN "PageRevision" working ON working."PageId"=p."Id" AND working."ActiveTo" IS NULL
          LEFT JOIN "PageRevision" published ON published."Id"=p."PublishedRevisionId"
          LEFT JOIN "PagePublication" publication ON publication."PageId"=p."Id" AND publication."ActiveTo" IS NULL
          CROSS JOIN actor a
           WHERE permission."UserId"=:actor AND NOT p."IsTrashed" AND NOT p."IsTemplate"
            AND (NOT :ai OR {{AccessSql.SpaceInAi("p.\"SpaceId\"")}})
            AND (p."PublishedRevisionId" IS NOT NULL
              OR permission."Role">=2 AND (p."OwnerId"=a."Id" OR a."IsWikiAdmin"))
        )
        """;

    public async Task<PageTagState> ReadAsync(Guid pageId, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery("""
            SELECT t."Id",t."Name" FROM "Tag" t
            JOIN "PageTag" pt ON pt."TagId"=t."Id" AND pt."ActiveTo" IS NULL
            WHERE pt."PageId"=:page ORDER BY t."NormalizedName",t."Id" LIMIT 20
            """).SetParameter("page", pageId).ListAsync<object[]>(ct);
        return new PageTagState(rows.Select(row => new TagDto((Guid)row[0], (string)row[1])).ToArray());
    }

    /// <summary>Current tags of many pages in one statement (the space tree of MCP); the caller has decided the pages are readable.</summary>
    public async Task<IReadOnlyDictionary<Guid, TagDto[]>> ReadManyAsync(Guid[] pageIds, CancellationToken ct)
    {
        if (pageIds.Length == 0) return new Dictionary<Guid, TagDto[]>();
        var rows = await session.CreateSQLQuery("""
            SELECT pt."PageId",t."Id",t."Name" FROM "Tag" t
            JOIN "PageTag" pt ON pt."TagId"=t."Id" AND pt."ActiveTo" IS NULL
            WHERE pt."PageId" IN (:pages) ORDER BY pt."PageId",t."NormalizedName",t."Id"
            """).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("Id", NHibernateUtil.Guid).AddScalar("Name", NHibernateUtil.String)
            .SetParameterList("pages", pageIds).ListAsync<object[]>(ct);
        return rows.GroupBy(row => (Guid)row[0]).ToDictionary(group => group.Key,
            group => group.Select(row => new TagDto((Guid)row[1], (string)row[2])).ToArray());
    }

    public async Task<Guid> ResolveAsync(string name, string normalizedName, UserAccount actor, CancellationToken ct)
    {
        // Ordered caller acquisition plus ON CONFLICT prevents catalog races across pages/spaces.
        await session.CreateSQLQuery("""
            INSERT INTO "Tag"("Id","Name","NormalizedName","CreatedAt","CreatedById")
            VALUES (:id,:name,:normalized,:created,:actor)
            ON CONFLICT ("NormalizedName") DO NOTHING
            """).SetParameter("id", Guid.CreateVersion7()).SetParameter("name", name)
            .SetParameter("normalized", normalizedName).SetParameter("created", DateTime.UtcNow, NHibernateUtil.UtcDateTime)
            .SetParameter("actor", actor.Id).ExecuteUpdateAsync(ct);
        return await session.CreateSQLQuery("""SELECT "Id" FROM "Tag" WHERE "NormalizedName"=:name""")
            .SetParameter("name", normalizedName).UniqueResultAsync<Guid>(ct);
    }

    public async Task<Dictionary<Guid, DateTime>> PreviousBoundariesAsync(Guid pageId, Guid[] tagIds, CancellationToken ct)
    {
        if (tagIds.Length == 0) return [];
        var rows = await session.CreateSQLQuery("""
            SELECT "TagId",max(coalesce("ActiveTo","ActiveFrom")) AS boundary FROM "PageTag"
            WHERE "PageId"=:page AND "TagId" IN (:tags) GROUP BY "TagId"
            """).AddScalar("TagId", NHibernateUtil.Guid).AddScalar("boundary", NHibernateUtil.UtcDateTime)
            .SetParameter("page", pageId).SetParameterList("tags", tagIds).ListAsync<object[]>(ct);
        return rows.ToDictionary(row => (Guid)row[0], row => (DateTime)row[1]);
    }

    /// <param name="aiScopeOnly">G5 of MCP: pages of a space switched out of AI and MCP count as not existing, so neither their tags nor they appear.</param>
    public async Task<TagSuggestions> SuggestAsync(UserAccount actor, string query, bool aiScopeOnly, CancellationToken ct)
    {
        var rows = await Bind(session.CreateSQLQuery(AccessiblePages + """

            SELECT DISTINCT t."Id",t."Name",t."NormalizedName" FROM "Tag" t
            JOIN "PageTag" pt ON pt."TagId"=t."Id" AND pt."ActiveTo" IS NULL
            JOIN accessible p ON p."Id"=pt."PageId"
            WHERE position(:query in t."NormalizedName")>0
            ORDER BY t."NormalizedName",t."Id" LIMIT 20
            """), actor, aiScopeOnly).SetParameter("query", query).ListAsync<object[]>(ct);
        return new TagSuggestions(rows.Select(row => new TagDto((Guid)row[0], (string)row[1])).ToArray());
    }

    public async Task<SearchResponse> BrowseAsync(UserAccount actor, ListTaggedPages request, string query, DateTime? changedSince, bool aiScopeOnly, CancellationToken ct)
    {
        var rows = await Bind(session.CreateSQLQuery(AccessiblePages + """
            , selected AS (
              SELECT p."Id",p."SpaceId",p."SpaceName",
                CASE WHEN :content='published' OR p.role<2 THEN p."PublishedTitle" ELSE p."WorkingTitle" END AS title,
                CASE WHEN :content='published' OR p.role<2 THEN p."PublishedAt"
                  ELSE greatest(p."WorkingAt",p."PublishedAt") END AS changed_at,
                p."PublishedAt" AS published_at,
                (:content<>'published' AND p.role>=2 AND p."WorkingRevisionId" IS DISTINCT FROM p."PublishedRevisionId") AS is_draft
              FROM accessible p JOIN "PageTag" pt ON pt."PageId"=p."Id" AND pt."ActiveTo" IS NULL
              WHERE pt."TagId"=:tag AND (:space IS NULL OR p."SpaceId"=:space)
                AND (:content<>'published' OR p."PublishedRevisionId" IS NOT NULL)
                AND (:content<>'drafts' OR p.role>=2 AND p."WorkingRevisionId" IS DISTINCT FROM p."PublishedRevisionId")
            )
            SELECT "Id","SpaceId","SpaceName",title,changed_at,published_at,is_draft FROM selected
            WHERE position(:query in lower(normalize(title,NFKC)))>0
              AND (:since IS NULL OR changed_at>=:since)
            ORDER BY CASE WHEN :latest THEN changed_at END DESC NULLS LAST,title,"Id"
            LIMIT :take OFFSET :skip
            """).AddScalar("Id", NHibernateUtil.Guid).AddScalar("SpaceId", NHibernateUtil.Guid)
            .AddScalar("SpaceName", NHibernateUtil.String).AddScalar("title", NHibernateUtil.String)
            .AddScalar("changed_at", NHibernateUtil.UtcDateTime).AddScalar("published_at", NHibernateUtil.UtcDateTime)
            .AddScalar("is_draft", NHibernateUtil.Boolean), actor, aiScopeOnly)
            .SetParameter("tag", request.TagId).SetParameter("space", request.SpaceId, NHibernateUtil.Guid)
            .SetParameter("content", request.Options.Content).SetParameter("query", query)
            .SetParameter("since", changedSince, NHibernateUtil.UtcDateTime)
            .SetParameter("latest", request.Options.Sort == "published")
            .SetParameter("take", request.Limit + 1).SetParameter("skip", request.Offset).ListAsync<object[]>(ct);
        var more = rows.Count > request.Limit;
        var items = rows.Take(request.Limit).Select(row => new SearchHit((Guid)row[0], (Guid)row[1], (string)row[2], (string)row[3], "")
        {
            ChangedAt = (DateTime?)row[4], PublishedAt = (DateTime?)row[5], IsDraft = (bool)row[6]
        }).ToArray();
        return new SearchResponse(items, more) { NextOffset = more ? request.Offset + request.Limit : null };
    }


    /// <summary>Existing tags of readable pages in one space, visibility applied before the bound.</summary>
    public async Task<IReadOnlyList<TagDto>> CandidatesInSpaceAsync(UserAccount actor, Guid spaceId, CancellationToken ct)
    {
        var rows = await Bind(session.CreateSQLQuery(AccessiblePages + """
            SELECT DISTINCT t."Id",t."Name",t."NormalizedName" FROM "Tag" t
            JOIN "PageTag" pt ON pt."TagId"=t."Id" AND pt."ActiveTo" IS NULL
            JOIN accessible p ON p."Id"=pt."PageId"
            WHERE p."SpaceId"=:space
            ORDER BY t."NormalizedName",t."Id" LIMIT 50
            """), actor, false).SetParameter("space", spaceId).ListAsync<object[]>(ct);
        return rows.Select(row => new TagDto((Guid)row[0], (string)row[1])).ToArray();
    }

    private static IQuery Bind(ISQLQuery query, UserAccount actor, bool aiScopeOnly) =>
        query.SetParameter("actor", actor.Id).SetParameter("stamp", actor.SecurityStamp).SetParameter("ai", aiScopeOnly);
}
