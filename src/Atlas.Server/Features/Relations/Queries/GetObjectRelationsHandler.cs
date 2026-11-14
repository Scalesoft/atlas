using Atlas.Server.Features.Objects.Domain;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Domain;
using Atlas.Server.Platform.Resources;

namespace Atlas.Server.Features.Relations.Queries;

/// <summary>
/// An object's relations and "Kde je použito" (spec §4.1, §13.3), for a reader of the object (404 otherwise, as a missing one). Every
/// edge of the object is kept, an end the reader may not read (or one trashed or gone) as a node with <c>readable=false</c> and
/// nothing else, so deleting never lies about the impact while nothing of the hidden end shows (spec §6). Depth 2 continues only
/// from readable neighbours. Constant statements per depth: one per relation source and one for the nodes.
/// </summary>
// shortcut: depth 2 is not capped; collapse or cap it when a graph view needs objects with thousands of neighbours.
public sealed class GetObjectRelationsHandler(IContentAccess access, RelationReader reader, UsageReasons reasons)
{
    public async Task<ObjectRelations> Handle(GetObjectRelations query, CancellationToken ct)
    {
        if (query.Depth is not (1 or 2))
        {
            throw new ApiException(400, "Hloubka vazeb je 1 nebo 2.", "invalid_depth");
        }
        var kinds = query.Kinds?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (kinds is { Count: 0 or > 16 } || kinds?.Any(kind => kind.Length > 32) == true)
        {
            throw new ApiException(400, "Druhy vazeb jsou čárkou oddělené názvy.", "invalid_kinds");
        }
        var root = query.ObjectId;
        var actor = await access.LockActorAsync(null, ct);
        var edges = await EdgesAsync([root], kinds, query.Drafts, ct);
        var graph = await reader.GraphAsync(actor, null, edges, [root], ct);
        if (!graph.IsVisible(root))
        {
            throw ObjectErrors.NotFound();
        }
        if (query.Depth == 2 && Ends(edges).Where(id => id != root && graph.IsVisible(id)).ToHashSet() is { Count: > 0 } next)
        {
            edges = edges.Concat(await EdgesAsync(next, kinds, query.Drafts, ct)).Distinct().ToArray();
            graph = await reader.GraphAsync(actor, null, edges, [root], ct);
        }
        // A draft's use is told only to whoever may edit the page that holds it (PageReader shows the working revision to editors).
        bool Told(ObjectEdge edge) => !edge.Draft || graph.Nodes.TryGetValue(edge.SourceId, out var page) && page.Visible && page.CanEdit;
        var own = edges.Distinct().Where(edge => edge.SourceId == root || edge.TargetId == root).ToArray();
        // A trashed end the reader could read is broken, not hidden (RelationGraph.Broken).
        bool Hidden(Guid end) => !graph.IsVisible(end) && !(graph.Nodes.TryGetValue(end, out var node) && node.Readable);
        var hidden = own.Count(edge => !Told(edge) || Hidden(edge.SourceId == root ? edge.TargetId : edge.SourceId));
        var kept = edges.Distinct().Where(edge => Told(edge) && (graph.IsVisible(edge.SourceId) || graph.IsVisible(edge.TargetId))).ToArray();
        var why = query.Reasons
            ? await reasons.ReadAsync(root, kept.Where(edge => RelationKinds.UsedEnd(edge) == root && graph.IsVisible(RelationKinds.PlaceOf(edge))).Take(50).ToArray(), ct)
            : new Dictionary<ObjectEdge, UsageReasons.Reason>();
        var nodes = Ends(kept).Prepend(root).Distinct()
            .Select(id => graph.Nodes.TryGetValue(id, out var node) && node.Visible
                ? new RelationNodeDto(id, node.TypeId, node.Title, node.SpaceId, node.SpaceName, true)
                : new RelationNodeDto(id, null, null, null, null, false))
            .OrderBy(node => node.Id != root).ThenBy(node => !node.Readable).ThenBy(node => node.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(node => node.Id)
            .ToArray();
        var shown = query.Depth == 2 ? kept.Where(why.ContainsKey).Concat(Merged(kept.Where(edge => !why.ContainsKey(edge)))) : kept;
        return new ObjectRelations(nodes, shown.OrderBy(edge => edge.Kind, StringComparer.Ordinal).ThenBy(edge => edge.SourceId).ThenBy(edge => edge.TargetId)
            .Select(edge => new RelationEdgeDto(edge.SourceId, edge.TargetId, edge.Kind, edge.Count, edge.Anchor, edge.Draft, why.GetValueOrDefault(edge)?.PartId,
                why.GetValueOrDefault(edge)?.Excerpt, why.GetValueOrDefault(edge)?.Card ?? false)).ToArray(), hidden);
    }

    private async Task<IReadOnlyList<ObjectEdge>> EdgesAsync(IReadOnlyCollection<Guid> around, IReadOnlySet<string>? kinds, bool drafts, CancellationToken ct)
    {
        var edges = await reader.EdgesAsync(Guid.Empty, around, ct, drafts);
        return kinds is null ? edges : edges.Where(edge => kinds.Contains(edge.Kind)).ToArray();
    }

    /// <summary>Depth 2 is a graph (the space's Vazby: one connector per ends and kind; MCP): the uses of one pair merge, their counts summed
    /// and the part (anchor) dropped, e.g. a deployment's edges to its catalog, one per requirement key. A use with a reason stays as it is.</summary>
    public static IEnumerable<ObjectEdge> Merged(IEnumerable<ObjectEdge> edges) =>
        edges.GroupBy(edge => (edge.SourceId, edge.TargetId, edge.Kind, edge.Draft))
            .Select(uses => uses.Skip(1).Any() ? uses.First() with { Count = uses.Sum(edge => edge.Count), Anchor = null } : uses.First());

    private static IEnumerable<Guid> Ends(IEnumerable<ObjectEdge> edges) => edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId });
}
