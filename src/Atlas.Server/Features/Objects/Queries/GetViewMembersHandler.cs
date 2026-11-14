using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Objects.Queries;

/// <summary>A view's membership: the view type names the objects (one query), their envelopes give type and version (one more).
/// Ids of unknown types, missing or trashed objects are dropped, so a session never subscribes what no module can authorize.</summary>
public sealed class GetViewMembersHandler
{
    private readonly ISession m_session;
    private readonly ObjectTypes m_types;

    public GetViewMembersHandler(ISession session, ObjectTypes types)
    {
        m_session = session;
        m_types = types;
    }

    public async Task<ViewMemberList> Handle(GetViewMembers query, CancellationToken ct)
    {
        if (m_types.View(query.Kind) is not { } view)
        {
            return new ViewMemberList(false, []);
        }
        var ids = (await view.MembersAsync(query.ViewId, ct)).Prepend(query.ViewId).Distinct().ToArray();
        var envelopes = (await m_session.QueryOver<ContentResource>()
                .WhereRestrictionOn(x => x.Id).IsIn(ids)
                .And(x => x.TrashedAt == null)
                .ListAsync(ct))
            .Where(envelope => m_types.Find(envelope.TypeId) is not null)
            .ToDictionary(envelope => envelope.Id);
        if (!envelopes.TryGetValue(query.ViewId, out var self) || self.TypeId != view.TypeId)
        {
            return new ViewMemberList(true, []);
        }
        return new ViewMemberList(true, ids.Where(envelopes.ContainsKey).Select(id => new ViewMember(id, envelopes[id].TypeId, envelopes[id].Version)).ToArray());
    }
}
