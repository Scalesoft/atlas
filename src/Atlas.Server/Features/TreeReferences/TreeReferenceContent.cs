using NHibernate;
using Atlas.Server.Features.Relations.Persistence;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Persistence;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.TreeReferences;

/// <summary>
/// Reference nodes in the space tree the pages module owns (spec 2026-10-27 space contents §5.1, §13): leaves under a page or at the
/// root, listed to a role in the space as boards are, changed by a Writer. Whether the account may read each node's target is decided
/// for all nodes in the one statement of <see cref="NodesAsync"/> by the rules of the relations (<see cref="RelationNodes"/>), never per
/// node. A node is trashed only with its page subtree, so the space's trash never lists one: it comes back with its batch.
/// </summary>
public sealed class TreeReferenceContent : IContentTreeParticipant, IContentPurgeParticipant
{
    public const string Type = "reference";

    private static readonly string NodesSql = $"""
        WITH node AS ({RelationNodes.Over("""SELECT t."TargetId" FROM "TreeReference" t WHERE t."SpaceId"=:tree AND NOT t."IsTrashed" """)})
        SELECT t."Id",t."ParentPageId",t."SortOrder",t."TargetId",t."Anchor",n."TypeId",CASE WHEN n.live AND n.readable THEN n.title END
        FROM "TreeReference" t LEFT JOIN node n ON n."Id"=t."TargetId"
        WHERE t."SpaceId"=:tree AND NOT t."IsTrashed"
        """;

    private readonly ISession m_session;
    private readonly ObjectTypes m_types;
    private readonly RelationNodes m_nodes;

    public TreeReferenceContent(ISession session, ObjectTypes types, RelationNodes nodes)
    {
        m_session = session;
        m_types = types;
        m_nodes = nodes;
    }

    public async Task<IReadOnlyList<ContentTreeNode>> NodesAsync(Guid spaceId, UserAccount actor, SpaceRole? spaceRole, CancellationToken ct)
    {
        if (spaceRole is not { } role)
        {
            return [];
        }
        var parameters = m_nodes.Parameters(actor, null, [], m_types.TypeIds());
        parameters["tree"] = spaceId;
        return await m_session.ReadRowsAsync(NodesSql, parameters, row => Node(row.GetGuid(0), row.IsDBNull(1) ? null : row.GetGuid(1), row.GetInt32(2),
            row.GetGuid(3), row.IsDBNull(4) ? null : row.GetString(4), row.IsDBNull(5) ? null : row.GetString(5), row.IsDBNull(6) ? null : row.GetString(6),
            role >= SpaceRole.Writer), ct);
    }

    /// <summary>The target's type and plain title when the account may read it (the rule of <see cref="NodesAsync"/>), else null.</summary>
    public async Task<(string TypeId, string Title)?> ReadableTargetAsync(UserAccount actor, Guid targetId, CancellationToken ct)
    {
        var (nodes, _) = await m_nodes.ReadAsync(actor, null, [targetId], m_types.TypeIds(), ct);
        return nodes.TryGetValue(targetId, out var node) && node.Visible ? (node.TypeId, node.Title) : null;
    }

    public static ContentTreeNode Node(Guid id, Guid? parentId, int sortOrder, Guid targetId, string? anchor, string? typeId, string? title, bool canEdit)
    {
        return new ContentTreeNode(Type, id, parentId, title, sortOrder, canEdit)
        {
            TargetId = targetId, Anchor = anchor, TargetTypeId = title is null ? null : typeId, Readable = title is not null
        };
    }

    public async Task<IReadOnlyList<ContentTreeSibling>> SiblingsAsync(Guid spaceId, Guid? parentPageId, CancellationToken ct)
    {
        var query = m_session.QueryOver<TreeReference>().Where(x => x.SpaceId == spaceId && !x.IsTrashed);
        query = parentPageId is { } parent ? query.Where(x => x.ParentPageId == parent) : query.Where(x => x.ParentPageId == null);
        return (await query.Select(x => x.Id, x => x.SortOrder).ListAsync<object[]>(ct)).Select(row => new ContentTreeSibling((Guid)row[0], (int)row[1])).ToArray();
    }

    public async Task ReorderAsync(IReadOnlyDictionary<Guid, int> sortOrders, CancellationToken ct)
    {
        foreach (var (id, order) in sortOrders)
        {
            (await m_session.GetAsync<TreeReference>(id, ct)).SortOrder = order;
        }
    }

    public Task TrashAsync(Guid spaceId, IReadOnlyCollection<Guid> pageIds, bool spaceRoot, ContentTrashStamp stamp, CancellationToken ct)
    {
        return UnderAsync(Followed("""
            UPDATE "TreeReference" SET "IsTrashed"=true, "TrashedAt"=:at, "TrashedById"=:by, "TrashBatchId"=:batch
            WHERE "SpaceId"=:space AND NOT "IsTrashed" AND ("ParentPageId" = ANY(CAST(string_to_array(:pages, ',') AS uuid[])) OR (:root AND "ParentPageId" IS NULL))
            """, "\"SpaceId\"=:space"), pageIds, query => query.SetParameter("space", spaceId).SetParameter("root", spaceRoot).SetParameter("at", stamp.TrashedAt).SetParameter("by", stamp.TrashedById)
                .SetParameter("batch", stamp.BatchId), ct, spaceRoot);
    }

    public Task RestoreAsync(Guid spaceId, IReadOnlyCollection<Guid> pageIds, bool spaceRoot, Guid batchId, CancellationToken ct)
    {
        return UnderAsync(Followed("""
            UPDATE "TreeReference" SET "IsTrashed"=false, "TrashedAt"=NULL, "TrashedById"=NULL, "TrashBatchId"=NULL
            WHERE "SpaceId"=:space AND "IsTrashed" AND "TrashBatchId"=:batch AND ("ParentPageId" = ANY(CAST(string_to_array(:pages, ',') AS uuid[])) OR (:root AND "ParentPageId" IS NULL))
            """, "\"SpaceId\"=:space"), pageIds, query => query.SetParameter("space", spaceId).SetParameter("root", spaceRoot).SetParameter("batch", batchId), ct, spaceRoot);
    }

    public string? Counted => null;

    public async Task<int> PurgeAsync(Guid spaceId, IReadOnlyCollection<Guid> pageIds, Guid? batchId, Guid actorId, CancellationToken ct)
    {
        // A link node has no trash of its own: it goes with its page, whatever batch it came with.
        if (batchId is null)
        {
            await m_session.CreateSQLQuery(Followed("""DELETE FROM "TreeReference" WHERE "SpaceId"=:space""", "\"SpaceId\"=:space"))
                .SetParameter("space", spaceId).ExecuteUpdateAsync(ct);
            return 0;
        }
        await UnderAsync(Followed("""
            DELETE FROM "TreeReference" WHERE "SpaceId"=:space AND "IsTrashed" AND "ParentPageId" = ANY(CAST(string_to_array(:pages, ',') AS uuid[]))
            """, "\"SpaceId\"=:space"), pageIds, query => query.SetParameter("space", spaceId), ct);
        return 0;
    }

    public Task MoveSpaceAsync(Guid sourceSpaceId, Guid targetSpaceId, IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
    {
        return UnderAsync(Followed("""
            UPDATE "TreeReference" SET "SpaceId"=:target
            WHERE "SpaceId"=:source AND "ParentPageId" = ANY(CAST(string_to_array(:pages, ',') AS uuid[]))
            """, "\"SpaceId\" IN (:source,:target)"), pageIds, query => query.SetParameter("source", sourceSpaceId).SetParameter("target", targetSpaceId), ct);
    }

    /// <summary>None: a node goes to the trash only with its page subtree and comes back with that batch; removing one deletes the link.</summary>
    public Task<IReadOnlyList<ContentTrashItem>> TrashedAsync(Guid spaceId, int take, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<ContentTrashItem>>([]);
    }

    /// <summary>The tree's token of <paramref name="spaces"/> moves when <paramref name="write"/> changed a node, in its statement, as for a
    /// node NHibernate writes (ContentRowEffects).</summary>
    private static string Followed(string write, string spaces) => $$"""
        WITH node AS ({{write}} RETURNING "Id")
        UPDATE "SpaceOrganization" SET "Version"="Version"+1 WHERE {{spaces}} AND EXISTS (SELECT 1 FROM node) RETURNING {{Pages.Live.PageChangeListener.TreeChanged("\"SpaceId\"")}}
        """;

    /// <summary>One statement over the nodes under <paramref name="pageIds"/>, as BoardContent writes its boards.</summary>
    private async Task UnderAsync(string sql, IReadOnlyCollection<Guid> pageIds, Func<ISQLQuery, IQuery> parameters, CancellationToken ct,
        bool spaceRoot = false)
    {
        if (pageIds.Count == 0 && !spaceRoot)
        {
            return;
        }
        await m_session.FlushAsync(ct);
        await parameters(m_session.CreateSQLQuery(sql)).SetParameter("pages", string.Join(',', pageIds)).ExecuteUpdateAsync(ct);
    }
}
