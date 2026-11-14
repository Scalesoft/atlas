using Atlas.Server.Features.Relations.Persistence;
using Atlas.Server.Platform.Resources;

namespace Atlas.Server.Features.Relations.Domain;

/// <summary>
/// The edges of the registry as one reader sees them (spec §3.2, §6): an edge is visible when the reader may read both live ends; it
/// is broken when one end is visible and the other is in the trash yet readable, or gone altogether (told only to a WikiAdmin, who
/// reads everything: to anyone else a missing end is as unreadable as a hidden one, so no id is confirmed or denied). The space read
/// as a whole (<paramref name="space"/>) is a visible end too: the root of its tree (RelationAnchor).
/// </summary>
public sealed class RelationGraph
{
    private readonly Guid? m_space;

    public RelationGraph(IReadOnlyDictionary<Guid, RelationNode> nodes, bool wikiAdmin, IEnumerable<ObjectEdge> edges, Guid? space = null)
    {
        Nodes = nodes;
        m_space = space;
        var visible = new List<ObjectEdge>();
        var broken = new List<(ObjectEdge, Guid)>();
        foreach (var edge in edges.Distinct())
        {
            var (source, target) = (IsVisible(edge.SourceId), IsVisible(edge.TargetId));
            if (source && target)
            {
                visible.Add(edge);
            }
            else if (source && IsBroken(edge.TargetId, wikiAdmin) || target && IsBroken(edge.SourceId, wikiAdmin))
            {
                broken.Add((edge, source ? edge.SourceId : edge.TargetId));
            }
        }
        Visible = visible;
        Broken = broken;
    }

    public IReadOnlyDictionary<Guid, RelationNode> Nodes { get; }

    public IReadOnlyList<ObjectEdge> Visible { get; }

    /// <summary>Each broken edge with its visible end, the object that holds the dead reference.</summary>
    public IReadOnlyList<(ObjectEdge Edge, Guid Holder)> Broken { get; }

    public bool IsVisible(Guid id) => id == m_space || Nodes.TryGetValue(id, out var node) && node.Visible;

    private bool IsBroken(Guid id, bool wikiAdmin) => Nodes.TryGetValue(id, out var node) ? !node.Live && node.Readable : wikiAdmin;
}
