using Atlas.Server.Platform.Resources;
using Atlas.Server.Platform.Persistence;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Relations.Persistence;

/// <summary>An object as a reader of relations sees it. <see cref="Readable"/> is decided as if the object were live, so a trashed
/// one the reader could read makes a broken edge, while an unreadable one stays unreadable (spec §6).</summary>
public sealed record RelationNode(Guid Id, string TypeId, string Scope, Guid? SpaceId, string? SpaceName, bool Live, bool Readable, bool CanEdit,
    string Title, Guid? ParentId, Guid? HostId, DateTime UpdatedAt, int Comments)
{
    public bool Visible => Live && Readable;
}

/// <summary>
/// The objects of a space and the ends of its edges, decided in one statement for one actor (the pattern of PageLinkStore and
/// RequirementIndex), never one authorization per object. The rules are those of the object's own reads: a page by
/// EffectivePageAccess and the list rule of PageListing (no templates in a space's listing; a template asked for by id is read as
/// the templates page lists it, for its „Použito na“); a board by the space role (under a page with its own access, and that page's role), a page's own board (Purpose) by
/// whoever may edit its page (read its page, for Board.PagePurpose) and has it listed (BoardAccess under PageListing: another's draft hides its board too); any other object
/// by its envelope's scope (ObjectScopeAccess; a private object is its owner's and grantees', no administrator's). Nothing of a space in the trash is readable (LiveSpace). Only the registered types count: an object of a disabled module is as absent as a
/// missing one. On MCP and for the assistant an object of a space switched out of AI is unreadable (G5, <see cref="AccessSql.SpaceInAi"/>).
/// </summary>
public sealed class RelationNodes(ISession session, IRequestChannel channel)
{
    private const string Ids = """SELECT x FROM unnest(CAST(string_to_array(:ids, ',') AS uuid[])) x""";

    private static readonly string Template = $$"""
        WITH {{AccessSql.ActorCte}}, roles AS MATERIALIZED (
          SELECT g."SpaceId",max(g."Role") AS "Role" FROM "EffectiveSpaceGrant" g
          WHERE g."UserId"=:actor OR g."GroupId" IN (SELECT m."GroupId" FROM "EffectiveGroupMember" m WHERE m."UserId"=:actor)
          GROUP BY g."SpaceId"
        ), candidate AS MATERIALIZED (
          SELECT r."Id" FROM "ContentResource" r WHERE :listing AND r."SpaceId"=:space AND r."TrashedAt" IS NULL
          UNION
          {ids}
        ), resource AS MATERIALIZED (
          SELECT r.* FROM candidate c JOIN "ContentResource" r ON r."Id"=c."Id"
          WHERE r."TypeId"=ANY(CAST(string_to_array(:types, ',') AS text[]))
        ), page_access AS MATERIALIZED (
          SELECT e."PageId",e."Role" FROM "EffectivePageAccess" e
          WHERE e."UserId"=:actor AND e."PageId" IN (
            SELECT "Id" FROM resource WHERE "TypeId"='atlas.page'
            UNION ALL
            SELECT b."ParentPageId" FROM resource r JOIN "Board" b ON b."Id"=r."Id" WHERE b."ParentPageId" IS NOT NULL)
        )
        SELECT r."Id",r."TypeId",r."Scope",r."SpaceId",s."Name" AS "SpaceName",r."TrashedAt" IS NULL AS live,
          coalesce(CASE
            WHEN p."Id" IS NOT NULL THEN (NOT p."IsTemplate" OR NOT :listing) AND pa."Role">=1
              AND (p."PublishedRevisionId" IS NOT NULL OR pa."Role">=2 AND (p."OwnerId"=a."Id" OR a."IsWikiAdmin"))
            WHEN b."Purpose" IS NOT NULL THEN ha."Role">=CASE WHEN b."Purpose"='projector.board' THEN 1 ELSE 2 END AND NOT hp."IsTrashed"
              AND (hp."PublishedRevisionId" IS NOT NULL OR hp."OwnerId"=a."Id" OR a."IsWikiAdmin")
            WHEN r."Scope"='private' THEN r."OwnerId"=a."Id" OR {{AccessSql.ObjectGrantRole("r.\"Id\"", "a.\"Id\"")}}>=1
            WHEN b."Id" IS NOT NULL THEN a."IsWikiAdmin" OR sr."Role">=1 AND {{UnderPage(1)}}
            WHEN r."Scope"='wiki' THEN true
            WHEN r."Scope"='space' THEN a."IsWikiAdmin" OR sr."Role">=1
          END,false) AND s."TrashedAt" IS NULL AND (NOT :ai OR r."SpaceId" IS NULL OR {{AccessSql.SpaceInAi("r.\"SpaceId\"")}}) AS readable,
          coalesce(CASE
            WHEN p."Id" IS NOT NULL THEN pa."Role">=2
            WHEN b."Purpose" IS NOT NULL THEN ha."Role">=2 AND NOT hp."IsTrashed"
              AND (hp."PublishedRevisionId" IS NOT NULL OR hp."OwnerId"=a."Id" OR a."IsWikiAdmin")
            WHEN r."Scope"='private' THEN r."OwnerId"=a."Id" OR {{AccessSql.ObjectGrantRole("r.\"Id\"", "a.\"Id\"")}}>=2
            WHEN b."Id" IS NOT NULL THEN a."IsWikiAdmin" OR sr."Role">=2 AND {{UnderPage(2)}}
            WHEN r."Scope"='wiki' THEN false -- the type decides (IWikiWriters): RelationReader.WikiWriterTypesAsync
            WHEN r."Scope"='space' THEN a."IsWikiAdmin" OR sr."Role">=2
          END,false) AS can_edit,
          coalesce(CASE WHEN pa."Role">=2 THEN w."Title" END,published."Title",w."Title",b."Title",r."Title",'') AS title,
          coalesce(p."ParentId",CASE WHEN b."Purpose" IS NULL THEN b."ParentPageId" END) AS parent,
          CASE WHEN b."Purpose" IS NOT NULL THEN b."ParentPageId" END AS host,
          r."UpdatedAt",
          CAST((SELECT count(*) FROM "Comment" c WHERE c."ObjectId"=r."Id" AND c."DeletedAt" IS NULL) AS int) AS comments,
          a."IsWikiAdmin" AS admin
        FROM resource r CROSS JOIN actor a
        LEFT JOIN "Space" s ON s."Id"=r."SpaceId"
        LEFT JOIN roles sr ON sr."SpaceId"=r."SpaceId"
        LEFT JOIN "Page" p ON r."TypeId"='atlas.page' AND p."Id"=r."Id"
        LEFT JOIN page_access pa ON pa."PageId"=p."Id"
        LEFT JOIN "PageRevision" w ON w."PageId"=p."Id" AND w."ActiveTo" IS NULL
        LEFT JOIN "PageRevision" published ON published."Id"=p."PublishedRevisionId"
        LEFT JOIN "Board" b ON r."TypeId"='atlas.board' AND b."Id"=r."Id"
        LEFT JOIN page_access ha ON ha."PageId"=b."ParentPageId"
        LEFT JOIN "Page" hp ON hp."Id"=b."ParentPageId"
        """;

    /// <summary>A space board under a page with its own access needs that page's <paramref name="role"/> too, under the draft rule (BoardAccess, M1).</summary>
    private static string UnderPage(int role) => $"""
        (hp."Id" IS NULL OR NOT hp."OwnAccess" OR ha."Role">={role} AND (hp."PublishedRevisionId" IS NOT NULL OR hp."OwnerId"=a."Id"))
        """;

    /// <summary>The statement of <see cref="ReadAsync"/> over the ids of :ids.</summary>
    public static readonly string Sql = Over(Ids);

    /// <summary>The statement over the ids <paramref name="ids"/> selects instead of :ids, for a reader that joins the nodes to rows of its
    /// own in one statement (TreeReferenceContent). Its parameters are those of <see cref="Parameters"/>.</summary>
    public static string Over(string ids) => Template.Replace("{ids}", ids);

    /// <param name="spaceId">With it, every live object of the space besides <paramref name="ids"/>.</param>
    /// <param name="types">The registered object types.</param>
    public Dictionary<string, object> Parameters(UserAccount actor, Guid? spaceId, IEnumerable<Guid> ids, IEnumerable<string> types) => new()
    {
        ["actor"] = actor.Id, ["stamp"] = actor.SecurityStamp, ["listing"] = spaceId is not null, ["space"] = spaceId ?? Guid.Empty,
        ["ids"] = string.Join(',', ids.Distinct()), ["types"] = string.Join(',', types),
        ["ai"] = channel.Current.IsAiBound
    };

    /// <returns>The nodes by id (a missing object, or one of a type no module registers, is absent) and whether the actor is a WikiAdmin.</returns>
    public async Task<(IReadOnlyDictionary<Guid, RelationNode> Nodes, bool WikiAdmin)> ReadAsync(UserAccount actor, Guid? spaceId, IEnumerable<Guid> ids,
        IEnumerable<string> types, CancellationToken ct)
    {
        var wikiAdmin = false;
        // A whole space's access decisions inflate the plan's cost estimate: at 10 000 objects PostgreSQL spent ~1.1 s JIT-compiling a
        // ~90 ms statement (as SearchSql.PrepareAsync); the other reads are by id and too small to trigger it.
        var rows = await session.ReadRowsAsync(Sql, Parameters(actor, spaceId, ids, types), row =>
        {
            wikiAdmin = row.GetBoolean(13);
            return new RelationNode(row.GetGuid(0), row.GetString(1), row.GetString(2), OptionalId(row, 3), row.IsDBNull(4) ? null : row.GetString(4),
                row.GetBoolean(5), row.GetBoolean(6), row.GetBoolean(7), row.GetString(8), OptionalId(row, 9), OptionalId(row, 10),
                DateTime.SpecifyKind(row.GetDateTime(11), DateTimeKind.Utc), row.GetInt32(12));
        }, ct, spaceId is null ? null : "SELECT set_config('jit','off',true)");
        return (rows.ToDictionary(node => node.Id), wikiAdmin);

        static Guid? OptionalId(System.Data.Common.DbDataReader row, int column) => row.IsDBNull(column) ? null : row.GetGuid(column);
    }
}
