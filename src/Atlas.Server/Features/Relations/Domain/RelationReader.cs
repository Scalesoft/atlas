using Atlas.Server.Features.Relations.Persistence;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Relations.Domain;

/// <summary>The registry's edges and the reader's view of their ends: one round trip for the statements of every relation source and
/// one for the nodes, whatever the size of the space.</summary>
public sealed class RelationReader(ISession session, ContentParticipants participants, RelationNodes nodes, ObjectTypes types, ObjectScopeAccess scopes)
{
    /// <param name="drafts">Also the uses only a page's working revision holds (ObjectEdge.Draft).</param>
    public async Task<IReadOnlyList<ObjectEdge>> EdgesAsync(Guid spaceId, IReadOnlyCollection<Guid>? around, CancellationToken ct, bool drafts = false)
    {
        return await RelationAnchor.ReadAllAsync(session, participants.Relations.Select(source => source.Statement).ToArray(),
            spaceId, around, drafts, ct);
    }

    /// <param name="spaceId">With it, every live object of the space joins the ends of <paramref name="edges"/> and <paramref name="also"/>.</param>
    public async Task<RelationGraph> GraphAsync(UserAccount actor, Guid? spaceId, IReadOnlyList<ObjectEdge> edges, IEnumerable<Guid> also, CancellationToken ct)
    {
        var (read, wikiAdmin) = await nodes.ReadAsync(actor, spaceId, edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId }).Concat(also),
            types.All().Select(type => type.TypeId), ct);
        return new RelationGraph(read, wikiAdmin, edges, spaceId);
    }

    /// <summary>Of <paramref name="typeIds"/>, the types whose wiki objects the actor may change (ObjectScopeAccess): a few statements
    /// per type, asked only for the types of wiki objects shown.</summary>
    public async Task<IReadOnlySet<string>> WikiWriterTypesAsync(UserAccount actor, IEnumerable<string> typeIds, CancellationToken ct)
    {
        var writers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var typeId in typeIds.Distinct())
        {
            if (types.Find(typeId) is { } type && await scopes.MayWriteWikiAsync(new ObjectActor(actor), type, ct))
            {
                writers.Add(typeId);
            }
        }
        return writers;
    }
}
