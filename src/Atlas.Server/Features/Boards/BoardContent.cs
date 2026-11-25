using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards;

/// <summary>
/// Boards in what the pages module owns (interaction spec §6): leaves of the space tree, favorites and recent visits, search
/// hits. A board inherits the roles of its space (BoardAccess), so who may list it is a Reader of the space; a page's own board is its
/// page editors' (the page's board, Board.PagePurpose, its readers' too; Listed) and never a node of the tree. Registered only with
/// the module on; without it the rows stay and nothing shows them.
/// </summary>
public sealed class BoardContent : IContentTreeParticipant, IContentLibraryParticipant, IContentSearchParticipant, IContentReferenceParticipant
{
    public const string Type = "board";

    private readonly ISession m_session;
    private readonly SpaceRoles m_roles;
    private readonly BoardAccess m_boards;
    private readonly AccessChanges m_changes;
    private readonly LiveRelay m_relay;

    public BoardContent(ISession session, SpaceRoles roles, BoardAccess boards, AccessChanges changes, LiveRelay relay)
    {
        m_session = session;
        m_roles = roles;
        m_boards = boards;
        m_changes = changes;
        m_relay = relay;
    }

    public async Task<IReadOnlyList<ContentTreeNode>> NodesAsync(Guid spaceId, UserAccount actor, SpaceRole? spaceRole, CancellationToken ct)
    {
        if (spaceRole is not { } role)
        {
            return [];
        }
        // A page's own board (Purpose) is reached from its page, never through the tree: it comes as the page's quiet mark
        // (ContentTreeNode.Attached), in the same query; Readable when the page's readers open it too (Board.PagePurpose).
        // A Writer edits a board under a page with its own access only when it edits that page too (M1, as BoardAccess decides).
        var rows = await m_session.QueryOver<Board>().Where(x => x.SpaceId == spaceId && !x.IsTrashed)
            .And(Expression.Sql(UnderReadablePage("{alias}", "?"), actor.Id, NHibernateUtil.Guid))
            .Select(Projections.Property<Board>(x => x.Id), Projections.Property<Board>(x => x.ParentPageId), Projections.Property<Board>(x => x.Title),
                Projections.Property<Board>(x => x.SortOrder), Projections.Property<Board>(x => x.Purpose),
                Projections.Conditional(Expression.Sql(UnderPage("{alias}", "?", 2), actor.Id, NHibernateUtil.Guid), Projections.Constant(true), Projections.Constant(false)))
            .ListAsync<object[]>(ct);
        return rows.Select(row => new ContentTreeNode(row[4] is null ? Type : ContentTreeNode.Attached, (Guid)row[0], (Guid?)row[1], (string)row[2], (int)row[3],
            role >= SpaceRole.Writer && (bool)row[5]) { Readable = row[4] is null or Board.PagePurpose }).ToArray();
    }

    // shortcut: a page created after a board ends its level gets its place from the page insert (ContentRowEffects: after the last page), so
    // before that board; let page creation place through PageSiblings when the order of new pages next to boards matters.
    public async Task<IReadOnlyList<ContentTreeSibling>> SiblingsAsync(Guid spaceId, Guid? parentPageId, CancellationToken ct)
    {
        var query = m_session.QueryOver<Board>().Where(x => x.SpaceId == spaceId && !x.IsTrashed);
        query = parentPageId is { } parent ? query.Where(x => x.ParentPageId == parent) : query.Where(x => x.ParentPageId == null);
        return (await query.Select(x => x.Id, x => x.SortOrder).ListAsync<object[]>(ct)).Select(row => new ContentTreeSibling((Guid)row[0], (int)row[1])).ToArray();
    }

    public async Task ReorderAsync(IReadOnlyDictionary<Guid, int> sortOrders, CancellationToken ct)
    {
        foreach (var (id, order) in sortOrders)
        {
            (await m_session.GetAsync<Board>(id, ct)).SortOrder = order;
        }
    }

    public async Task TrashAsync(Guid spaceId, IReadOnlyCollection<Guid> pageIds, bool spaceRoot, ContentTrashStamp stamp, CancellationToken ct)
    {
        var trashed = await UnderAsync(Followed("""
            UPDATE "Board" SET "IsTrashed"=true, "TrashedAt"=:at, "TrashedById"=:by, "TrashBatchId"=:batch, "UpdatedAt"=:at
            WHERE "SpaceId"=:space AND NOT "IsTrashed" AND ("ParentPageId" = ANY(CAST(string_to_array(:pages, ',') AS uuid[])) OR (:root AND "ParentPageId" IS NULL))
            """, "\"SpaceId\"=:space"), pageIds, query => query.SetParameter("space", spaceId).SetParameter("root", spaceRoot).SetParameter("at", stamp.TrashedAt).SetParameter("by", stamp.TrashedById)
                .SetParameter("batch", stamp.BatchId), ct, spaceRoot);
        Revalidate(trashed);
    }

    public async Task RestoreAsync(Guid spaceId, IReadOnlyCollection<Guid> pageIds, bool spaceRoot, Guid batchId, CancellationToken ct)
    {
        await UnderAsync(Followed("""
            UPDATE "Board" SET "IsTrashed"=false, "TrashedAt"=NULL, "TrashedById"=NULL, "TrashBatchId"=NULL, "UpdatedAt"=now()
            WHERE "SpaceId"=:space AND "IsTrashed" AND "TrashBatchId"=:batch AND ("ParentPageId" = ANY(CAST(string_to_array(:pages, ',') AS uuid[])) OR (:root AND "ParentPageId" IS NULL))
            """, "\"SpaceId\"=:space"), pageIds, query => query.SetParameter("space", spaceId).SetParameter("root", spaceRoot).SetParameter("batch", batchId), ct, spaceRoot);
    }

    public async Task MoveSpaceAsync(Guid sourceSpaceId, Guid targetSpaceId, IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
    {
        // Their envelope follows in the same statement; who may read them now is the target space's roles.
        var moved = await UnderAsync(Followed("""
            UPDATE "Board" SET "SpaceId"=:target, "UpdatedAt"=now()
            WHERE "SpaceId"=:source AND "ParentPageId" = ANY(CAST(string_to_array(:pages, ',') AS uuid[]))
            """, "\"SpaceId\" IN (:source,:target)"), pageIds, query => query.SetParameter("source", sourceSpaceId).SetParameter("target", targetSpaceId), ct);
        Revalidate(moved);
    }

    public async Task<IReadOnlyList<ContentTrashItem>> TrashedAsync(Guid spaceId, int take, CancellationToken ct)
    {
        var boards = await m_session.QueryOver<Board>().Where(x => x.SpaceId == spaceId && x.IsTrashed)
            .OrderBy(x => x.TrashedAt).Desc.ThenBy(x => x.Id).Asc.Take(take).ListAsync(ct);
        return boards.Select(board => new ContentTrashItem(Type, board.Id, board.ParentPageId, board.Title, board.TrashedAt!.Value, board.TrashedById!.Value,
            board.TrashBatchId!.Value)).ToArray();
    }

    public async Task<Guid?> SpaceOfAsync(Guid resourceId, CancellationToken ct)
    {
        var space = await m_session.QueryOver<Board>().Where(x => x.Id == resourceId).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid>(ct);
        return space == Guid.Empty ? null : space;
    }

    public async Task<UserAccount> RequireReadableAsync(Guid resourceId, CancellationToken ct)
    {
        return (await m_boards.OpenAsync(resourceId, false, [], ct)).Actor;
    }

    public async Task<IReadOnlyList<ContentLibraryEntry>> ListAsync(string kind, UserAccount actor, int take, CancellationToken ct)
    {
        // Without a space role an account may still edit single pages and keep their boards (Listed).
        var spaces = await ReadableSpacesAsync(actor, ct);
        var (personal, orderedAt) = kind == "favorites"
            ? ("""JOIN "PageFavorite" f ON f."PageId"=b."Id" AND f."UserId"=:actor AND f."ActiveTo" IS NULL""", """f."ActiveFrom" """)
            : ("""JOIN "PageVisit" v ON v."PageId"=b."Id" AND v."UserId"=:actor""", """v."LastVisitedAt" """);
        var rows = await m_session.CreateSQLQuery($$"""
                SELECT b."Id", b."SpaceId", s."Name", b."Title", {{orderedAt}},
                       (SELECT 1 FROM "PageFavorite" x WHERE x."PageId"=b."Id" AND x."UserId"=:actor AND x."ActiveTo" IS NULL) IS NOT NULL,
                       (SELECT y."LastVisitedAt" FROM "PageVisit" y WHERE y."PageId"=b."Id" AND y."UserId"=:actor)
                FROM "Board" b JOIN "Space" s ON s."Id"=b."SpaceId" {{personal}}
                WHERE NOT b."IsTrashed" AND {{Listed}}
                ORDER BY 5 DESC, b."Title", b."Id" LIMIT :take
                """)
            .SetParameter("actor", actor.Id).SetParameter("all", spaces is null).SetParameter("spaces", string.Join(',', spaces ?? []))
            .SetParameter("take", take)
            .ListAsync<object[]>(ct);
        return rows.Select(row => new ContentLibraryEntry(Type, (Guid)row[0], (Guid)row[1], (string)row[2], (string)row[3], (DateTime)row[4], (bool)row[5],
            (DateTime?)row[6])).ToArray();
    }

    public async Task<IReadOnlyList<ContentSearchHit>> SearchAsync(UserAccount actor, string query, string normalized, Guid? spaceId, int limit, CancellationToken ct)
    {
        var expression = SearchText.QueryExpression(query).Expression;
        var spaces = await ReadableSpacesAsync(actor, ct);
        if (expression.Length == 0)
        {
            return [];
        }
        var rows = await m_session.CreateSQLQuery($$"""
                SELECT b."Id", b."SpaceId", s."Name", b."Title", d."Body"
                FROM "BoardSearchDocument" d JOIN "Board" b ON b."Id"=d."BoardId" JOIN "Space" s ON s."Id"=b."SpaceId",
                     to_tsquery('simple', :expression) q
                WHERE d."Vector" @@ q AND NOT b."IsTrashed" AND {{Listed}}
                  AND (CAST(:space AS uuid) IS NULL OR b."SpaceId"=CAST(:space AS uuid))
                ORDER BY ts_rank(d."Vector", q) DESC, b."Title", b."Id" LIMIT :take
                """)
            .SetParameter("expression", expression).SetParameter("actor", actor.Id).SetParameter("all", spaces is null).SetParameter("spaces", string.Join(',', spaces ?? []))
            .SetParameter("space", spaceId?.ToString(), NHibernateUtil.String).SetParameter("take", limit)
            .ListAsync<object[]>(ct);
        return rows.Select(row => new ContentSearchHit(Type, (Guid)row[0], (Guid)row[1], (string)row[2], (string)row[3],
            SearchText.Snippet((string)row[4] is { Length: > 0 } body ? body : (string)row[3], normalized))).ToArray();
    }

    /// <summary>A link to a board in a page shows the board's title to who finds it (Listed), as a page link does (UX PG4-4).</summary>
    public async Task<IReadOnlyList<ContentReference>> ResolveAsync(UserAccount actor, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        var spaces = await ReadableSpacesAsync(actor, ct);
        var rows = await m_session.CreateSQLQuery($$"""
                SELECT b."Id", b."Title" FROM "Board" b
                WHERE b."Id" IN (:ids) AND NOT b."IsTrashed" AND {{Listed}}
                """)
            .SetParameterList("ids", ids).SetParameter("actor", actor.Id).SetParameter("all", spaces is null).SetParameter("spaces", string.Join(',', spaces ?? []))
            .ListAsync<object[]>(ct);
        return rows.Select(row => new ContentReference(Type, (Guid)row[0], (string)row[1])).ToArray();
    }

    /// <summary>Which boards :actor finds: a board of the space tree in the spaces it reads (:spaces, every one when :all), under a page with
    /// own access only when it reads that page (UnderReadablePage, M1); a page's own
    /// board (Purpose) as BoardAccess opens it, by its page alone (review S4-2, S4-4): the page out of the trash, a page role of an editor
    /// (of a reader for the page's board, Board.PagePurpose), and an unpublished page only its owner's, the list draft rule
    /// (PageListing.Listed; a WikiAdmin reads every page).</summary>
    private static readonly string Listed = $"""
        (b."Purpose" IS NULL AND (:all OR b."SpaceId" = ANY(CAST(string_to_array(:spaces, ',') AS uuid[])) AND {UnderReadablePage("b", ":actor")})
         OR b."Purpose" IS NOT NULL AND EXISTS (SELECT 1 FROM "Page" p WHERE p."Id"=b."ParentPageId" AND NOT p."IsTrashed" AND (:all
           OR (p."PublishedRevisionId" IS NOT NULL OR p."OwnerId"=:actor)
              AND EXISTS (SELECT 1 FROM "EffectivePageAccess" e WHERE e."PageId"=p."Id" AND e."UserId"=:actor
                AND e."Role">=CASE WHEN b."Purpose"='{Board.PagePurpose}' THEN 1 ELSE 2 END))))
        """;

    /// <summary>A board's parent page, when it has its own access, is one <paramref name="actor"/> reads (M1; the page rule of
    /// BoardAccess: EffectivePageAccess and the draft rule, a WikiAdmin reads every page). True for a board under no such page.</summary>
    public static string UnderReadablePage(string board, string actor) => UnderPage(board, actor, 1);

    /// <summary><see cref="UnderReadablePage"/> with the page role the account needs on that page: 1 reads it, 2 edits it.</summary>
    private static string UnderPage(string board, string actor, int role) => $$"""
        NOT EXISTS (SELECT 1 FROM "Page" hp WHERE hp."Id"={{board}}."ParentPageId" AND hp."OwnAccess" AND NOT EXISTS (
          SELECT 1 FROM "EffectivePageAccess" e JOIN "UserAccount" ua ON ua."Id"=e."UserId" WHERE e."PageId"=hp."Id" AND e."UserId"={{actor}} AND e."Role">={{role}}
            AND (hp."PublishedRevisionId" IS NOT NULL OR hp."OwnerId"=ua."Id" OR ua."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=ua."Id"))))
        """;

    /// <summary>The spaces the account reads boards of (any role); null for a WikiAdmin, who reads them all.</summary>
    private async Task<IReadOnlyCollection<Guid>?> ReadableSpacesAsync(UserAccount actor, CancellationToken ct)
    {
        return await m_roles.IsWikiAdminAsync(actor, ct) ? null : (await m_roles.EffectiveAsync(actor, ct)).Keys.ToArray();
    }

    /// <summary>One statement over the boards under <paramref name="pageIds"/>: the ids travel as one text parameter, as
    /// PageSubtree.ChildrenOf does, so its shape does not depend on their number.</summary>
    private async Task<IReadOnlyList<Guid>> UnderAsync(string sql, IReadOnlyCollection<Guid> pageIds, Func<ISQLQuery, IQuery> parameters, CancellationToken ct,
        bool spaceRoot = false)
    {
        if (pageIds.Count == 0 && !spaceRoot)
        {
            return [];
        }
        // Written behind the session's back, after what it holds is written; the page handlers that call this load no board.
        await m_session.FlushAsync(ct);
        var query = parameters(m_session.CreateSQLQuery(sql).AddScalar("Id", NHibernateUtil.Guid)).SetParameter("pages", string.Join(',', pageIds));
        return (await query.ListAsync<Guid>(ct)).ToArray();
    }

    /// <summary>The boards <paramref name="update"/> changes, with what follows them as for a board NHibernate writes (ContentRowEffects):
    /// their envelope's space and trash stamp, and the tree's token of <paramref name="spaces"/> when any changed.</summary>
    private static string Followed(string update, string spaces) => $$"""
        WITH board AS ({{update}} RETURNING "Id", "SpaceId", "TrashedAt"),
          envelope AS (UPDATE "ContentResource" r SET "SpaceId"=board."SpaceId", "TrashedAt"=board."TrashedAt" FROM board WHERE r."Id"=board."Id"),
          tree AS (UPDATE "SpaceOrganization" SET "Version"="Version"+1 WHERE {{spaces}} AND EXISTS (SELECT 1 FROM board) RETURNING {{Pages.Live.PageChangeListener.TreeChanged("\"SpaceId\"")}})
        SELECT "Id" FROM board
        """;

    /// <summary>Subscribers of boards that changed space or went to the trash are decided again after the commit (Revoked).</summary>
    private void Revalidate(IReadOnlyCollection<Guid> boards)
    {
        if (boards.Count > 0)
        {
            m_changes.AfterCommit(() => m_relay.RefreshAsync(null, CancellationToken.None));
        }
    }
}
