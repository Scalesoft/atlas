using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.Mcp.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>Whether a page has a working revision newer than its publication and unsaved live edits, only for pages the actor may edit
/// (document role ≥ 2 now) and sees in lists (PageListing.Listed); never who edits. The space must take part in AI and MCP (G5),
/// otherwise 404 like an unreadable one.</summary>
public sealed class GetMcpPageStatesHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly McpVisibility m_visibility;

    public GetMcpPageStatesHandler(ISession session, IContentAccess access, McpVisibility visibility)
    {
        m_session = session;
        m_access = access;
        m_visibility = visibility;
    }

    public async Task<McpPageStates> Handle(GetMcpPageStates query, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(query.SpaceId, ct);
        await m_visibility.RequireSpaceAsync(query.SpaceId, ct);
        if (query.PageIds.Count == 0)
        {
            return new McpPageStates([]);
        }
        // The list draft rule, as in the tree the tool takes its ids from: a state says that the page exists and whether someone is
        // typing in it, which another author's never published draft does not tell an account that edits it only by role.
        var actorId = actor.Id;
        var spaceId = query.SpaceId;
        var listed = await m_session.QueryOver<PageAccessEntry>()
            .Where(x => x.UserId == actorId && x.SpaceId == spaceId && !x.IsTrashed)
            .WhereRestrictionOn(x => x.PageId).IsIn(query.PageIds.Distinct().ToArray())
            .And(PageListing.Listed(Projections.Property<PageAccessEntry>(x => x.IsPublished), Projections.Property<PageAccessEntry>(x => x.OwnerId),
                Projections.Property<PageAccessEntry>(x => x.Role), actor))
            .Select(x => x.PageId)
            .ListAsync<Guid>(ct);
        if (listed.Count == 0)
        {
            return new McpPageStates([]);
        }
        var rows = await m_session.CreateSQLQuery("""
            SELECT p."Id" AS id,
              (p."PublishedRevisionId" IS NOT NULL AND w."Id" <> p."PublishedRevisionId") AS unpublished,
              EXISTS (SELECT 1 FROM "PageCollaboration" c WHERE c."PageId" = p."Id" AND COALESCE(c."LastSavedSequence", 0) < c."Sequence") AS live
            FROM "Page" p
            JOIN "PageRevision" w ON w."PageId" = p."Id" AND w."ActiveTo" IS NULL
            JOIN "EffectivePageAccess" e ON e."PageId" = p."Id" AND e."UserId" = :actor AND e."Role" >= 2
            WHERE p."Id" IN (:ids) AND p."SpaceId" = :space AND NOT p."IsTrashed"
            """)
            .AddScalar("id", NHibernateUtil.Guid)
            .AddScalar("unpublished", NHibernateUtil.Boolean)
            .AddScalar("live", NHibernateUtil.Boolean)
            .SetParameter("actor", actor.Id)
            .SetParameter("space", query.SpaceId)
            .SetParameterList("ids", listed)
            .ListAsync<object[]>(ct);
        return new McpPageStates(rows.Select(row => new McpPageState((Guid)row[0], (bool)row[1], (bool)row[2])).ToArray());
    }
}
