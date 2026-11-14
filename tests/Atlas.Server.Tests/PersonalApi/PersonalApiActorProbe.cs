using Atlas.Server.Features.PersonalApi;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Tests.PersonalApi;

/// <summary>Loads the actor into the first-level cache, rotates its stamp behind the session's back, then takes the
/// personal API FOR UPDATE lock. With the real HttpCurrentActor the request actor IS that cached instance, so only a
/// stamp captured before the refresh can notice the rotation.</summary>
public sealed record ProbeStalePersonalApiActor(Guid ActorId);

public sealed class ProbeStalePersonalApiActorHandler
{
    private readonly ISession m_session;
    private readonly AtlasDatabase m_database;
    private readonly PersonalApiActor m_actor;

    public ProbeStalePersonalApiActorHandler(ISession session, AtlasDatabase database, PersonalApiActor actor)
    {
        m_session = session;
        m_database = database;
        m_actor = actor;
    }

    public async Task<Guid> Handle(ProbeStalePersonalApiActor message, CancellationToken ct)
    {
        await m_session.GetAsync<UserAccount>(message.ActorId, ct);
        using (var other = m_database.OpenSession())
        using (var transaction = other.BeginTransaction())
        {
            await other.CreateSQLQuery("UPDATE \"UserAccount\" SET \"SecurityStamp\"='rotated' WHERE \"Id\"=:id")
                .SetParameter("id", message.ActorId)
                .ExecuteUpdateAsync(ct);
            await transaction.CommitAsync(ct);
        }
        var locked = await m_actor.LockAsync(ct);
        return locked.Id;
    }
}
