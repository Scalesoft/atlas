using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Objects.Queries;

/// <summary>
/// The decision of the object channel <c>obj:{id}</c> for the handshake account, as the board and page channels decide theirs:
/// Forbidden for every object when the session no longer proves the account, else each object's type decides (NotFound for a
/// missing or trashed object and one of a type no enabled module registers). Plain reads without content locks; the envelopes
/// in one query, then one decision per object in this scope.
/// </summary>
// shortcut: each object is decided by its type one by one (a few queries each, all in this one transaction); add a batch decision to
// IObjectType when boards with hundreds of cards make joins or sweeps slow.
public sealed class AuthorizeObjectViewerHandler
{
    private readonly ISession m_session;
    private readonly ObjectTypes m_types;

    public AuthorizeObjectViewerHandler(ISession session, ObjectTypes types)
    {
        m_session = session;
        m_types = types;
    }

    public async Task<ObjectViewerAccess> Handle(AuthorizeObjectViewer query, CancellationToken ct)
    {
        var actor = await m_session.GetAsync<UserAccount>(query.UserId, ct);
        if (actor is null || !actor.IsActive || actor.SecurityStamp != query.SecurityStamp
            || query.SessionId is { } session && await BrowserSessionRevocations.IsRevokedAsync(m_session, session, ct))
        {
            return new ObjectViewerAccess(query.ObjectIds.Distinct().ToDictionary(id => id, _ => LiveAccess.Forbidden));
        }
        var envelopes = (await m_session.QueryOver<ContentResource>().WhereRestrictionOn(x => x.Id).IsIn(query.ObjectIds.Distinct().ToArray()).ListAsync(ct))
            .ToDictionary(envelope => envelope.Id);
        var result = new Dictionary<Guid, LiveAccess>();
        foreach (var id in query.ObjectIds.Distinct())
        {
            var access = ObjectAccess.None;
            if (envelopes.TryGetValue(id, out var envelope) && envelope.TrashedAt is null && m_types.Find(envelope.TypeId) is { } type)
            {
                access = await type.AuthorizeAsync(new ObjectActor(actor), id, ct);
            }
            result[id] = access == ObjectAccess.None ? LiveAccess.NotFound : new LiveAccess(LiveAccessOutcome.Allowed, access == ObjectAccess.Write, envelope!.Version);
        }
        return new ObjectViewerAccess(result);
    }
}
