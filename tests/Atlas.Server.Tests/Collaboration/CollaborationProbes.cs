using Atlas.Server.Features.Collaboration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>Loads the actor into the first-level cache, rotates its stamp behind the session's back, then authorizes the relay. Only a refresh under the account lock can notice.</summary>
public sealed record ProbeStaleCollaborator(Guid PageId, Guid ActorId);

public sealed class ProbeStaleCollaboratorHandler
{
    private readonly ISession m_session;
    private readonly AtlasDatabase m_database;
    private readonly CollaborationAccess m_access;

    public ProbeStaleCollaboratorHandler(ISession session, AtlasDatabase database, CollaborationAccess access)
    {
        m_session = session;
        m_database = database;
        m_access = access;
    }

    public async Task<Guid> Handle(ProbeStaleCollaborator message, CancellationToken ct)
    {
        var cached = await m_session.GetAsync<UserAccount>(message.ActorId, ct);
        // The stamp the hub handshake would carry, captured before the rotation.
        var expectedStamp = cached!.SecurityStamp;
        using (var other = m_database.OpenSession())
        using (var transaction = other.BeginTransaction())
        {
            await other.CreateSQLQuery("UPDATE \"UserAccount\" SET \"SecurityStamp\"='rotated' WHERE \"Id\"=:id")
                .SetParameter("id", message.ActorId)
                .ExecuteUpdateAsync(ct);
            await transaction.CommitAsync(ct);
        }
        var page = await m_access.RequireWriterAsync(message.PageId, message.ActorId, expectedStamp, ct);
        return page.Id;
    }
}
