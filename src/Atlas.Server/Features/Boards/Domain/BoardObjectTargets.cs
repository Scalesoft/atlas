using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Relations.Domain;
using Atlas.Server.Features.Relations.Persistence;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards.Domain;

/// <summary>
/// The objects of object cards as an account may see them, decided by the rules of each object's own reads (an object of a type no
/// enabled module registers, a trashed or missing one is simply absent): the envelopes in one query and the decision for all of them in
/// the one statement of the relations (RelationNodes), never one per card; a type's wiki writers (IWikiWriters) once per type.
/// On MCP (G5) an object of a space switched out of AI is absent too.
/// </summary>
public sealed class BoardObjectTargets
{
    private readonly ISession m_session;
    private readonly ObjectTypes m_types;
    private readonly RelationNodes m_nodes;
    private readonly RelationReader m_reader;
    private readonly McpVisibility m_visibility;
    private readonly IRequestChannel m_channel;

    public BoardObjectTargets(ISession session, ObjectTypes types, RelationNodes nodes, RelationReader reader, McpVisibility visibility, IRequestChannel channel)
    {
        m_session = session;
        m_types = types;
        m_nodes = nodes;
        m_reader = reader;
        m_visibility = visibility;
        m_channel = channel;
    }

    public async Task<IReadOnlyDictionary<Guid, BoardTargetDto>> ReadableAsync(UserAccount account, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, BoardTargetDto>();
        if (ids.Count == 0)
        {
            return result;
        }
        var envelopes = await m_session.QueryOver<ContentResource>().WhereRestrictionOn(x => x.Id).IsIn(ids.Distinct().ToArray()).And(x => x.TrashedAt == null).ListAsync(ct);
        var excluded = m_channel.Current.IsAiBound ? await m_visibility.ExcludedSpacesAsync(envelopes.Select(x => x.SpaceId).OfType<Guid>().ToArray(), ct) : null;
        envelopes = envelopes.Where(x => excluded is null || x.SpaceId is not { } space || !excluded.Contains(space)).ToList();
        var (nodes, _) = await m_nodes.ReadAsync(account, null, envelopes.Select(envelope => envelope.Id), m_types.TypeIds(), ct);
        var visible = envelopes.Where(envelope => nodes.TryGetValue(envelope.Id, out var node) && node.Visible).ToArray();
        var wikiWriters = await m_reader.WikiWriterTypesAsync(account, visible.Where(envelope => envelope.Scope == ObjectScopes.Wiki).Select(envelope => envelope.TypeId), ct);
        foreach (var envelope in visible)
        {
            var title = envelope.Title ?? (await m_types.Find(envelope.TypeId)!.ProjectAsync(envelope.Id, ct))?.Title;
            var canEdit = nodes[envelope.Id].CanEdit || envelope.Scope == ObjectScopes.Wiki && wikiWriters.Contains(envelope.TypeId);
            result[envelope.Id] = new BoardTargetDto(envelope.TypeId, envelope.Id, title, null, null, true)
            {
                Version = envelope.Version, CanEdit = canEdit, Data = envelope.Data is { } data ? JsonNode.Parse(data)!.AsObject() : null
            };
        }
        return result;
    }
}
