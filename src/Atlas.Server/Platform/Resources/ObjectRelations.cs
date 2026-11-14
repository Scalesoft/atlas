using Atlas.Server.Platform.Persistence;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Resources;

/// <summary>
/// A module's relations between objects (spec 2026-10-27 §3.3, §13.2): the read side of the relation registry. Each source answers
/// with one SQL statement over its own tables and decides no rights: the caller keeps an edge only when it may read both ends (or
/// shows an unreadable end as a lock). A disabled module registers none; a new kind is a new registration (<see cref="ContentParticipants.Relations"/>).
/// </summary>
// shortcut: edges are read from each module's tables on every request, no derived edge table; add one when measurements ask (spec §2.4).
public interface IObjectRelationSource
{
    /// <summary>The statement a reader sends with every source's in one round trip (<see cref="RelationAnchor.ReadAllAsync"/>): its edges with
    /// at least one end among the live objects of the space (<c>around</c> null), or among <c>around</c> (the space is then ignored). An end may
    /// be trashed or missing: that edge is broken. For the whole space only, an end may be the space itself, the root of its tree
    /// (TreeReferenceRelations). <c>:drafts</c> asks also for the uses only a page's working revision holds (<see cref="ObjectEdge.Draft"/>,
    /// plan 3.1 M5); a source that reads no drafts answers as without them.</summary>
    RelationStatement Statement { get; }
}

/// <summary>A relation source's statement after <see cref="RelationAnchor.Cte"/> (parameters <see cref="RelationAnchor.Parameters"/>) and
/// how a row of it reads.</summary>
public sealed record RelationStatement(string Sql, Func<System.Data.Common.DbDataReader, ObjectEdge> Read);

/// <param name="Count">Several cards of one object on one board are one edge.</param>
/// <param name="Anchor">The place inside the source (a heading id) when every card of the edge shows the same one.</param>
/// <param name="Draft">Only the source page's working revision holds it, not its publication (asked with drafts only).</param>
public sealed record ObjectEdge(Guid SourceId, Guid TargetId, string Kind, int Count = 1, string? Anchor = null, bool Draft = false);

/// <summary>The kinds of P1 (spec §13.2); a module adding one names it here and regenerates tests/fixtures/content/platform-manifest.json
/// (PlatformManifestTests), which the web's relationsApi.test.ts compares with its kindInfo.</summary>
public static class RelationKinds
{
    public const string Link = "link";
    public const string Placement = "placement";
    public const string Attached = "attached";
    public const string Mention = "mention";
    public const string Reference = "reference";
    /// <summary>A page created from a template (PageTemplateRelations): the template is used by the page.</summary>
    public const string Template = "template";
    /// <summary>A requirement waits on a contact (Projector, plan 3.1 §2.3): from the page to the contact.</summary>
    public const string Party = "party";
    /// <summary>A deployment uses a catalog requirement (Projector): from the page to the catalog page, the key as the anchor.</summary>
    public const string Catalog = "catalog";

    /// <summary>The end an edge uses (relationsApi.ts kindInfo.used): a card and an attached board lead from it, the rest to it.</summary>
    public static Guid UsedEnd(ObjectEdge edge) => edge.Kind is Placement or Attached ? edge.SourceId : edge.TargetId;

    /// <summary>The place an edge uses its <see cref="UsedEnd"/> in: the board, the page or the tree's parent.</summary>
    public static Guid PlaceOf(ObjectEdge edge) => edge.Kind is Placement or Attached ? edge.TargetId : edge.SourceId;
}

/// <summary>The common start of a relation source's statement: the CTE <c>anchor</c> holds the objects whose edges are asked for.</summary>
public static class RelationAnchor
{
    /// <summary>Live objects of :space when :whole, plus the ids of :around (comma-separated). Index IX_ContentResource_Space.</summary>
    public const string Cte = """
        WITH anchor AS MATERIALIZED (
          SELECT r."Id" FROM "ContentResource" r WHERE :whole AND r."SpaceId"=:space AND r."TrashedAt" IS NULL
          UNION
          SELECT x FROM unnest(CAST(string_to_array(:around, ',') AS uuid[])) x
        )
        """;

    /// <summary>Runs <paramref name="sql"/> (CTEs after <c>anchor</c> start with a comma, then the select) as a read straight from the
    /// driver: a space's edges are tens of thousands of rows. <c>:drafts</c> is there for a source that reads them.</summary>
    public static Task<List<ObjectEdge>> ReadAsync(ISession session, string sql, Guid spaceId, IReadOnlyCollection<Guid>? around,
        Func<System.Data.Common.DbDataReader, ObjectEdge> read, CancellationToken ct, bool drafts = false)
    {
        return session.ReadRowsAsync(Cte + sql, Parameters(spaceId, around, drafts), read, ct);
    }

    /// <summary>The edges of every statement, in their order, from one command: one round trip whatever the number of sources.</summary>
    public static async Task<List<ObjectEdge>> ReadAllAsync(ISession session, IReadOnlyList<RelationStatement> statements, Guid spaceId,
        IReadOnlyCollection<Guid>? around, bool drafts, CancellationToken ct)
    {
        var edges = new List<ObjectEdge>();
        if (statements.Count == 0)
        {
            return edges;
        }
        await using var command = session.Connection.CreateCommand();
        session.GetCurrentTransaction()?.Enlist(command);
        command.CommandText = string.Join(";\n", statements.Select(statement => Cte + statement.Sql));
        foreach (var (name, value) in Parameters(spaceId, around, drafts))
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            foreach (var statement in statements)
            {
                while (await reader.ReadAsync(ct))
                {
                    edges.Add(statement.Read(reader));
                }
                await reader.NextResultAsync(ct);
            }
        }
        catch
        {
            await PreparedStatements.ForgetAsync(command);
            throw;
        }
        return edges;
    }

    public static Dictionary<string, object> Parameters(Guid spaceId, IReadOnlyCollection<Guid>? around, bool drafts = false) => new()
    {
        ["whole"] = around is null, ["space"] = spaceId, ["around"] = around is null ? "" : string.Join(',', around), ["drafts"] = drafts
    };
}
