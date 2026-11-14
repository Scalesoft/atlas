using Atlas.Server.Features.YouTrack;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Tests.YouTrack;

/// <summary>Loads the actor into the first-level cache, rotates its stamp behind the session's back, then runs
/// <see cref="YouTrackActor.RequireAsync"/>. With the real HttpCurrentActor the request actor IS that cached
/// instance, so only a stamp captured before the refresh can notice the rotation.</summary>
public sealed record ProbeStaleYouTrackActor(Guid ActorId);

public sealed class ProbeStaleYouTrackActorHandler
{
    private readonly ISession m_session;
    private readonly AtlasDatabase m_database;
    private readonly YouTrackActor m_actor;

    public ProbeStaleYouTrackActorHandler(ISession session, AtlasDatabase database, YouTrackActor actor)
    {
        m_session = session;
        m_database = database;
        m_actor = actor;
    }

    public async Task<Guid> Handle(ProbeStaleYouTrackActor message, CancellationToken ct)
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
        var current = await m_actor.RequireAsync(false, ct);
        return current.Id;
    }
}
