using NHibernate;
using Atlas.Server.Features.PageLinks.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageLinks;

/// <summary>Reads of the PageLink projection. The projection holds identities only; every read decides visibility in SQL on the
/// actor's current rights before any limit or count, so a page the actor may not read leaves no title, count or existence.</summary>
public sealed class PageLinkStore(ISession session)
{
    private const string Actor = "WITH " + AccessSql.ActorCte;

    /// <summary>Published, untrashed, non-template pages whose current publication links to the target and which the actor may
    /// read (published scope: page role ≥ 1). A row of an older publication never shows. The target's space comes first.</summary>
    public async Task<PageLinkList> LinksHereAsync(UserAccount actor, Page target, int limit, CancellationToken ct)
    {
        var rows = await Bind(session.CreateSQLQuery(Actor + """
            , linking AS MATERIALIZED (
              SELECT p."Id",p."SpaceId",s."Name" AS "SpaceName",r."Title"
              FROM "PageLink" l
              JOIN "Page" p ON p."Id"=l."SourcePageId" AND p."PublishedRevisionId"=l."SourceRevisionId"
              JOIN "PageRevision" r ON r."Id"=l."SourceRevisionId"
              JOIN "Space" s ON s."Id"=p."SpaceId"
              CROSS JOIN actor a
              WHERE l."TargetPageId"=:target AND NOT p."IsTrashed" AND NOT p."IsTemplate"
                AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e
                  WHERE e."UserId"=a."Id" AND e."PageId"=p."Id" AND e."Role">=1))
            )
            SELECT "Id","SpaceId","SpaceName","Title",count(*) OVER () AS total FROM linking
            ORDER BY "SpaceId"<>:space,"Title","Id" LIMIT :take
            """), actor)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("SpaceId", NHibernateUtil.Guid)
            .AddScalar("SpaceName", NHibernateUtil.String).AddScalar("Title", NHibernateUtil.String)
            .AddScalar("total", NHibernateUtil.Int64)
            .SetParameter("target", target.Id).SetParameter("space", target.SpaceId).SetParameter("take", limit)
            .ListAsync<object[]>(ct);
        var items = rows.Select(row => new LinkingPage((Guid)row[0], (Guid)row[1], (string)row[2], (string)row[3])).ToArray();
        return new PageLinkList(items, rows.Count == 0 ? 0 : (int)(long)rows[0][4]);
    }

    /// <summary>
    /// Least cosine similarity of the centroids for a related page (O17: the "meaning only" floor of hybrid search, 0.45;
    /// tuned by step M). Applied when reading, so tuning needs no recompute of the lists.
    /// </summary>
    public const float RelatedMinSimilarity = 0.45f;

    /// <summary>
    /// R1: the target's precomputed neighbours in the <b>active</b> generation, filtered now (plan §12, §3): the neighbour is
    /// published, not in the trash, not a template, indexed with its current publication, readable by the actor (WikiAdmin or
    /// page role ≥ 1, the actor active with the request's stamp), and both its space and the target's are included in AI
    /// (<see cref="AccessSql.SpaceInAi"/> once per space). Pages linked from the target's current publication or linking to the target
    /// from theirs are left out (they are in Odkazuje sem or in the text). Most similar first, at most <paramref name="limit"/>;
    /// nothing is counted, so an unreadable page leaves no trace.
    /// </summary>
    public async Task<IReadOnlyList<RelatedPage>> RelatedAsync(UserAccount actor, Page target, int limit, CancellationToken ct)
    {
        var rows = await Bind(session.CreateSQLQuery(Actor + $$"""
            , neighbors AS MATERIALIZED (
              SELECT n."GenerationId",n."NeighborPageId" AS "Id",n."Rank" FROM "PageNeighbor" n
              JOIN "IndexGeneration" g ON g."Id"=n."GenerationId" AND g."State"='Active'
              WHERE n."PageId"=:target AND n."Similarity">=:min AND {{AccessSql.SpaceInAi(":space")}}
            ), linked AS MATERIALIZED (
              SELECT l."TargetPageId" AS "Id" FROM "PageLink" l
              JOIN "Page" s ON s."Id"=l."SourcePageId" AND s."PublishedRevisionId"=l."SourceRevisionId"
              WHERE l."SourcePageId"=:target
              UNION
              SELECT l."SourcePageId" FROM "PageLink" l
              JOIN "Page" s ON s."Id"=l."SourcePageId" AND s."PublishedRevisionId"=l."SourceRevisionId"
              WHERE l."TargetPageId"=:target
            ), candidates AS MATERIALIZED (
              SELECT p."Id",p."SpaceId",p."PublishedRevisionId",c."Rank" FROM neighbors c
              JOIN "Page" p ON p."Id"=c."Id"
              JOIN "KnowledgePage" k ON k."GenerationId"=c."GenerationId" AND k."PageId"=p."Id" AND k."RevisionId"=p."PublishedRevisionId"
              CROSS JOIN actor a
              WHERE NOT p."IsTrashed" AND NOT p."IsTemplate" AND p."Id" NOT IN (SELECT "Id" FROM linked)
                AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e
                  WHERE e."UserId"=a."Id" AND e."PageId"=p."Id" AND e."Role">=1))
            ), included AS MATERIALIZED (
              SELECT x."SpaceId" FROM (SELECT DISTINCT "SpaceId" FROM candidates) x WHERE {{AccessSql.SpaceInAi("x.\"SpaceId\"")}}
            )
            SELECT c."Id",c."SpaceId",s."Name" AS "SpaceName",r."Title" FROM candidates c
            JOIN included i ON i."SpaceId"=c."SpaceId"
            JOIN "Space" s ON s."Id"=c."SpaceId"
            JOIN "PageRevision" r ON r."Id"=c."PublishedRevisionId"
            ORDER BY c."Rank",c."Id" LIMIT :take
            """), actor)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("SpaceId", NHibernateUtil.Guid)
            .AddScalar("SpaceName", NHibernateUtil.String).AddScalar("Title", NHibernateUtil.String)
            .SetParameter("target", target.Id).SetParameter("space", target.SpaceId)
            .SetParameter("min", RelatedMinSimilarity).SetParameter("take", limit)
            .ListAsync<object[]>(ct);
        return rows.Select(row => new RelatedPage((Guid)row[0], (Guid)row[1], (string)row[2], (string)row[3])).ToArray();
    }

    /// <summary>Contacts of <see cref="RelatedPeopleAsync"/>: enough to reorder an <c>@</c> list or a name's people.</summary>
    public const int RelatedPeopleLimit = 20;

    /// <summary>
    /// P15 boost: the wiki and space contacts the current publications of <paramref name="related"/> (<see cref="RelatedAsync"/>'s
    /// answer, already filtered on the reader's rights and AI) name, the most similar page's first. The editor ranks people named on
    /// pages close in meaning a little higher; it never shows this list. Trashed and private contacts are left out.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> RelatedPeopleAsync(IReadOnlyList<RelatedPage> related, CancellationToken ct)
    {
        if (related.Count == 0)
        {
            return [];
        }
        var rows = await session.CreateSQLQuery("""
            SELECT m."SourcePageId",m."ObjectId" FROM "PageObjectMention" m
            JOIN "Page" p ON p."Id"=m."SourcePageId" AND p."PublishedRevisionId"=m."SourceRevisionId"
            JOIN "ContentResource" c ON c."Id"=m."ObjectId" AND c."TypeId"=:type AND c."TrashedAt" IS NULL AND c."Scope" IN (:wiki,:spaceScope)
            WHERE m."SourcePageId" IN (:pages)
            """)
            .AddScalar("SourcePageId", NHibernateUtil.Guid).AddScalar("ObjectId", NHibernateUtil.Guid)
            .SetParameterList("pages", related.Select(page => page.Id).ToList()).SetParameter("type", Contacts.ContactObjectType.Id)
            .SetParameter("wiki", Platform.Resources.ObjectScopes.Wiki).SetParameter("spaceScope", Platform.Resources.ObjectScopes.Space)
            .ListAsync<object[]>(ct);
        var rank = related.Select((page, index) => (page.Id, index)).ToDictionary(x => x.Id, x => x.index);
        return rows.OrderBy(row => rank[(Guid)row[0]]).ThenBy(row => (Guid)row[1]).Select(row => (Guid)row[1]).Distinct().Take(RelatedPeopleLimit).ToArray();
    }

    // The list draft rule of PageListing.Listed over EffectivePageAccess: a readable publication (role >= 1), or an unpublished
    // page the actor may edit and owns (or any, as WikiAdmin). Trash is excluded. Joined after the candidates are known, so the
    // view filters by the actor and the page.
    private const string ListedCondition = """
        NOT p."IsTrashed" AND e."Role">=1
          AND (p."PublishedRevisionId" IS NOT NULL OR e."Role">=2 AND (p."OwnerId"=a."Id" OR a."IsWikiAdmin"))
        """;

    /// <summary>Link targets of a revision that readers will not reach. A target the actor may not list is "unavailable" and
    /// never named, whether it is missing, trashed or hidden; an unpublished draft the actor may list is "unpublished" with its
    /// working title. Links to the page itself are ignored, as in the projection.</summary>
    public async Task<IReadOnlyList<LinkFinding>> CheckAsync(UserAccount actor, Guid pageId, Guid revisionId, CancellationToken ct)
    {
        var content = await session.CreateSQLQuery("""SELECT "ContentJson" AS json FROM "PageRevision" WHERE "Id"=:revision""")
            .AddScalar("json", NHibernateUtil.String).SetParameter("revision", revisionId).UniqueResultAsync<string?>(ct);
        var linked = PageLinkTargets.Of(content).Where(target => target != pageId).ToArray();
        if (linked.Length == 0)
        {
            return [];
        }
        var rows = await Bind(session.CreateSQLQuery(Actor + """
            , targets AS MATERIALIZED (
              SELECT t AS "Id" FROM unnest(CAST(string_to_array(:targets, ',') AS uuid[])) t
            ), listed AS MATERIALIZED (
              SELECT p."Id",p."PublishedRevisionId",w."Title"
              FROM targets c JOIN "Page" p ON p."Id"=c."Id"
              JOIN "EffectivePageAccess" e ON e."PageId"=p."Id" AND e."UserId"=:actor
              JOIN "PageRevision" w ON w."PageId"=p."Id" AND w."ActiveTo" IS NULL
              CROSS JOIN actor a
              WHERE
            """ + " " + ListedCondition + """
            )
            SELECT c."Id",CASE WHEN v."Id" IS NULL THEN 'unavailable' ELSE 'unpublished' END AS state,
              CASE WHEN v."Id" IS NOT NULL THEN v."Title" END AS title
            FROM targets c LEFT JOIN listed v ON v."Id"=c."Id"
            WHERE v."Id" IS NULL OR v."PublishedRevisionId" IS NULL
            ORDER BY state,c."Id"
            """), actor)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("state", NHibernateUtil.String).AddScalar("title", NHibernateUtil.String)
            .SetParameter("targets", string.Join(',', linked))
            .ListAsync<object[]>(ct);
        return rows.Select(row => new LinkFinding((Guid)row[0], (string)row[1], (string?)row[2])).ToArray();
    }

    /// <summary>The given pages the actor may list, with the title it would open: the working title for an editor, the
    /// published one otherwise (PageReader). No bodies are read and no opening is recorded.</summary>
    public async Task<IReadOnlyList<PageReference>> ResolveAsync(UserAccount actor, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var rows = await Bind(session.CreateSQLQuery(Actor + """
            SELECT p."Id",CASE WHEN e."Role">=2 THEN w."Title" ELSE published."Title" END AS title
            FROM "EffectivePageAccess" e JOIN "Page" p ON p."Id"=e."PageId"
            JOIN "PageRevision" w ON w."PageId"=p."Id" AND w."ActiveTo" IS NULL
            LEFT JOIN "PageRevision" published ON published."Id"=p."PublishedRevisionId"
            CROSS JOIN actor a
            WHERE e."UserId"=a."Id" AND e."PageId" IN (:ids) AND
            """ + " " + ListedCondition), actor)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("title", NHibernateUtil.String)
            .SetParameterList("ids", ids)
            .ListAsync<object[]>(ct);
        return rows.Select(row => new PageReference((Guid)row[0], (string)row[1])).ToArray();
    }

    /// <summary>
    /// The pages the actor may list (as <see cref="ResolveAsync"/>, templates left out) whose title — the one it would open — equals
    /// one of <paramref name="titles"/> ignoring case: for <c>[[Název]]</c> in Markdown written over the API or MCP (K2); with
    /// <paramref name="inAiScope"/> (MCP) only in spaces that take part in AI and MCP (G5, <see cref="AccessSql.SpaceInAi"/>). Each row is the
    /// asked title, the page and its title, at most <paramref name="perTitle"/> per title (a caller needs only to tell one match from
    /// several); a title with no row matches nothing the actor may list, which never tells a missing page from an unreadable or excluded one.
    /// </summary>
    public async Task<IReadOnlyList<(string Asked, Guid Id, string Title)>> FindByTitleAsync(UserAccount actor,
        IReadOnlyCollection<string> titles, int perTitle, bool inAiScope, CancellationToken ct)
    {
        if (titles.Count == 0)
        {
            return [];
        }
        var rows = await Bind(session.CreateSQLQuery(Actor + """
            , wanted AS MATERIALIZED (
              SELECT DISTINCT x AS "Asked",lower(x) AS "Key" FROM unnest(CAST(ARRAY[:titles] AS text[])) x
            ), candidates AS MATERIALIZED (
              SELECT p."Id",CASE WHEN e."Role">=2 THEN w."Title" ELSE published."Title" END AS title
              FROM "EffectivePageAccess" e JOIN "Page" p ON p."Id"=e."PageId"
              JOIN "PageRevision" w ON w."PageId"=p."Id" AND w."ActiveTo" IS NULL
              LEFT JOIN "PageRevision" published ON published."Id"=p."PublishedRevisionId"
              CROSS JOIN actor a
              WHERE e."UserId"=a."Id" AND NOT p."IsTemplate"
                AND (lower(w."Title") IN (SELECT "Key" FROM wanted) OR lower(published."Title") IN (SELECT "Key" FROM wanted)) AND
            """ + " " + ListedCondition + $$"""
            ), included AS MATERIALIZED (
              SELECT c."Id",c.title FROM candidates c JOIN "Page" p ON p."Id"=c."Id" WHERE NOT :ai OR {{AccessSql.SpaceInAi("p.\"SpaceId\"")}}
            )
            SELECT "Asked","Id",title FROM (
              SELECT x."Asked",c."Id",c.title,row_number() OVER (PARTITION BY x."Asked" ORDER BY c."Id") AS n
              FROM wanted x JOIN included c ON lower(c.title)=x."Key"
            ) ranked WHERE n<=:take ORDER BY "Asked","Id"
            """), actor)
            .AddScalar("Asked", NHibernateUtil.String).AddScalar("Id", NHibernateUtil.Guid).AddScalar("title", NHibernateUtil.String)
            .SetParameterList("titles", titles.Distinct(StringComparer.Ordinal).ToArray())
            .SetParameter("take", perTitle).SetParameter("ai", inAiScope)
            .ListAsync<object[]>(ct);
        return rows.Select(row => ((string)row[0], (Guid)row[1], (string)row[2])).ToArray();
    }

    private static ISQLQuery Bind(ISQLQuery query, UserAccount actor)
    {
        query.SetParameter("actor", actor.Id).SetParameter("stamp", actor.SecurityStamp);
        return query;
    }
}
