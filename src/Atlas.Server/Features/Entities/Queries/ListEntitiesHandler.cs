using System.Data.Common;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.Relations.Persistence;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Platform.Persistence;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Entities.Queries;

/// <summary>
/// The entity query (plan §14.3) over the catalog. An occurrence counts only from a resource the actor may read, decided in SQL on its
/// current rights before any count or limit, by the one rule of <see cref="RelationNodes"/> over the resources that have occurrences in
/// the scope (never over every page of the wiki): of a readable page a reader gets the published revision's occurrences, a writer the
/// working one's, each named <c>working</c> only when the published revision lacks its identity (UX3-3); a directory contact is read by its envelope's scope, a private one by its owner and grantees (ObjectScopeAccess). On
/// MCP and for the assistant a space switched out of AI is left out (G5). Entities without a readable occurrence do not exist for the
/// actor, and their attributes come from the readable occurrences only. Constant statements: the actor lock and one query for the
/// total and the page with its occurrences.
/// </summary>
public sealed class ListEntitiesHandler(ISession session, IContentAccess access, RelationNodes nodes)
{
    public static readonly string Sql = $"""
        WITH RECURSIVE subtree AS (
          SELECT p."Id" FROM "Page" p WHERE :scope='subtree' AND p."Id"=:id
          UNION ALL
          SELECT c."Id" FROM "Page" c JOIN subtree s ON c."ParentId"=s."Id"
        ), node AS ({RelationNodes.Over("""
          SELECT DISTINCT o."ResourceId" FROM "EntityOccurrence" o WHERE o."Kind"=:kind
            AND (:scope='instance' OR :scope='resource' AND o."ResourceId"=:id
              OR :scope='space' AND o."ResourceId" IN (SELECT r."Id" FROM "ContentResource" r WHERE r."SpaceId"=:id)
              OR :scope='subtree' AND o."ResourceId" IN (SELECT "Id" FROM subtree))
          """)}
        ), readable AS MATERIALIZED (
          SELECT o."IdentityKey" AS identity,o."Attributes" AS attributes,o."Anchor" AS anchor,o."ResourceId" AS resource,
            CASE WHEN o."RevisionKind"='working' AND NOT EXISTS (SELECT 1 FROM "EntityOccurrence" p WHERE p."ResourceId"=o."ResourceId"
              AND p."RevisionKind"='published' AND p."Kind"=o."Kind" AND p."IdentityKey"=o."IdentityKey") THEN 'working' ELSE 'published' END AS revision,
            n."TypeId" AS type,n.title,n."SpaceId" AS space,n."SpaceName" AS space_name
          FROM node n JOIN "EntityOccurrence" o ON o."ResourceId"=n."Id" AND o."Kind"=:kind
            AND o."RevisionKind"=CASE WHEN n."TypeId"=:contentType AND n.can_edit THEN 'working' ELSE 'published' END
          WHERE n.live AND n.readable
        ), matched AS MATERIALIZED (
          SELECT identity,min(lower(public.unaccent(COALESCE(attributes->>'name','')))) AS sort FROM readable x
          WHERE (:identity='' OR identity=:identity) AND NOT EXISTS (SELECT 1 FROM unnest(string_to_array(:words, E'\n')) w
            WHERE NOT EXISTS (SELECT 1 FROM jsonb_each_text(x.attributes) f WHERE lower(public.unaccent(f.value)) LIKE w))
          GROUP BY identity
        ), paged AS (
          SELECT identity,sort FROM matched ORDER BY sort,identity LIMIT :take OFFSET :skip
        )
        SELECT (SELECT count(*) FROM matched) AS total,r.identity,CAST(r.attributes AS text) AS attributes,r.anchor,r.revision,r.resource,r.type,r.title,
          r.space,r.space_name
        FROM (SELECT 1) one LEFT JOIN (paged g JOIN readable r ON r.identity=g.identity) ON true
        ORDER BY g.sort,g.identity,r.type,r.revision DESC,r.title,r.resource,r.anchor
        """;

    public async Task<EntityList> Handle(ListEntities query, CancellationToken ct)
    {
        if (query.Kind != EntityKinds.Contact)
        {
            throw new ApiException(400, "Druh entity je contact.", "invalid_kind");
        }
        if (!ListEntities.Scopes.Contains(query.Scope) || (query.Scope == "instance") != (query.ScopeId is null))
        {
            throw new ApiException(400, "Rozsah je instance, nebo space, subtree či resource s id.", "invalid_scope");
        }
        if (query.Q is { Length: > 200 } || query.Identity is { Length: > 500 })
        {
            throw new ApiException(400, "Filtr je příliš dlouhý.", "invalid_filter");
        }
        var actor = await access.LockActorAsync(null, ct);
        var parameters = Parameters(nodes.Parameters(actor, null, [], [PagesAtlasModule.ContentType, ContactObjectType.Id]), query);
        var rows = await session.ReadRowsAsync(Sql, parameters, row => new Row(row.GetInt64(0), Text(row, 1), Text(row, 2), Text(row, 3), Text(row, 4),
            row.IsDBNull(5) ? Guid.Empty : row.GetGuid(5), Text(row, 6), Text(row, 7), row.IsDBNull(8) ? null : row.GetGuid(8), Text(row, 9)), ct);
        var items = rows.Where(row => row.Identity is not null).GroupBy(row => row.Identity!)
            .Select(occurrences => Merge(occurrences, query.All || query.Identity is not null)).ToArray();
        return new EntityList(items, rows.Count == 0 ? 0 : (int)rows[0].Total, (int)parameters["skip"] / (int)parameters["take"] + 1, (int)parameters["take"]);

        static string? Text(DbDataReader row, int column) => row.IsDBNull(column) ? null : row.GetString(column);
    }

    /// <summary>The statement's parameters: those of <see cref="RelationNodes"/> (no space listing, no ids of its own) and the query's.</summary>
    public static Dictionary<string, object> Parameters(Dictionary<string, object> relations, ListEntities query)
    {
        var page = query.All ? 1 : Math.Max(1, query.Page);
        var size = query.All ? ListEntities.ExportLimit : ListEntities.PageSize;
        relations["kind"] = query.Kind;
        relations["scope"] = query.Scope;
        relations["id"] = query.ScopeId ?? Guid.Empty;
        relations["words"] = string.Join('\n', SearchText.LookupWords(query.Q)
            .Select(word => "%" + word.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));
        relations["contentType"] = PagesAtlasModule.ContentType;
        relations["take"] = size;
        relations["skip"] = (page - 1) * size;
        relations["identity"] = query.Identity ?? "";
        return relations;
    }

    /// <summary>
    /// One entity from its readable occurrences, each source once. When a directory contact holds the identity, the directory alone
    /// speaks for the person: its fields and lists are the entity's, and a page that names the same e-mail adds only a source, never a
    /// phone, an address or any field the directory leaves empty, so a page writer cannot plant data on a directory card (SEC1-3).
    /// Without one, the first occurrence's fields (the newest title order) and the lists (e-mails, phones) of all of them.
    /// </summary>
    private static EntityItem Merge(IGrouping<string, Row> occurrences, bool allSources)
    {
        var directory = occurrences.Where(row => row.Type == ContactObjectType.Id).ToArray();
        var attributes = new JsonObject();
        foreach (var row in directory.Length > 0 ? directory : occurrences.AsEnumerable())
        {
            foreach (var (key, value) in JsonNode.Parse(row.Attributes!)!.AsObject())
            {
                if (value is JsonArray list && attributes[key] is JsonArray merged)
                {
                    foreach (var item in list.Where(item => !merged.Any(existing => JsonNode.DeepEquals(existing, item))))
                    {
                        merged.Add(item!.DeepClone());
                    }
                }
                else if (!attributes.ContainsKey(key))
                {
                    attributes[key] = value?.DeepClone();
                }
            }
        }
        var sources = occurrences.Select(row => new EntitySource(row.Resource, row.Type!, row.Title ?? "", row.Space, row.SpaceName, row.Anchor, row.Revision!))
            .Distinct().ToArray();
        // The directory contact of the person: its own occurrence, else the one the pages' references name.
        var contactId = directory.Select(row => (Guid?)row.Resource).FirstOrDefault()
            ?? (Guid.TryParse(attributes["objectId"]?.GetValue<string>(), out var referenced) ? referenced : null);
        return new EntityItem(occurrences.Key, EntityKinds.Contact, attributes, allSources ? sources : sources[..Math.Min(sources.Length, ListEntities.SourceLimit)],
            sources.Length, contactId);
    }

    private sealed record Row(long Total, string? Identity, string? Attributes, string? Anchor, string? Revision, Guid Resource, string? Type,
        string? Title, Guid? Space, string? SpaceName);
}
