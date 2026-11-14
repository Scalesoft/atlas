using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Tests.Platform;

public sealed record ProbeSession;
// Wolverine codegen recurses forever when a handler returns the type it also injects; the wrapper breaks that cycle.
public sealed record ProbeSessionResult(ISession Session);
public sealed record RenameSpaceThenFail(Guid SpaceId, string Name);

public sealed class ProbeSessionHandler
{
    private readonly ISession m_session;

    public ProbeSessionHandler(ISession session)
    {
        m_session = session;
    }

    public ProbeSessionResult Handle(ProbeSession message)
    {
        if (m_session.GetCurrentTransaction() is not { IsActive: true })
        {
            throw new InvalidOperationException("Middleware did not open a transaction.");
        }
        return new ProbeSessionResult(m_session);
    }
}

public sealed class RenameSpaceThenFailHandler
{
    private readonly ISession m_session;

    public RenameSpaceThenFailHandler(ISession session)
    {
        m_session = session;
    }

    public async Task Handle(RenameSpaceThenFail message, CancellationToken ct)
    {
        var space = await m_session.GetAsync<Space>(message.SpaceId, ct);
        space!.Name = message.Name;
        await m_session.FlushAsync(ct);
        throw new ApiException(418, "probe failure");
    }
}

public sealed record ProbeAccess(Guid? LockSpaceId, Guid RequireSpaceId, SpaceRole Required);

public sealed class ProbeAccessHandler
{
    private readonly IContentAccess m_access;

    public ProbeAccessHandler(IContentAccess access)
    {
        m_access = access;
    }

    public async Task<SpaceRole> Handle(ProbeAccess message, CancellationToken ct)
    {
        await m_access.LockActorAsync(message.LockSpaceId, ct);
        return await m_access.RequireSpaceAsync(message.RequireSpaceId, message.Required, ct);
    }
}

/// <summary>Locks the page's own space unless LockPageSpace is false; then LockSpaceId (possibly none) is locked instead.</summary>
public sealed record ProbePageAccess(Guid PageId, AccessOperation Operation, bool LockPageSpace = true, Guid? LockSpaceId = null);

public sealed class ProbePageAccessHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;

    public ProbePageAccessHandler(ISession session, IContentAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<PageAuthorization> Handle(ProbePageAccess message, CancellationToken ct)
    {
        var page = await m_session.GetAsync<Page>(message.PageId, ct) ?? throw new ApiException(404, "Stránka nebyla nalezena.");
        await m_access.LockActorAsync(message.LockPageSpace ? page.SpaceId : message.LockSpaceId, ct);
        await m_session.RefreshAsync(page, ct);
        return await m_access.RequirePageAsync(page, message.Operation, ct);
    }
}

/// <summary>Loads the actor into the first-level cache, rotates its stamp behind the session's back, then locks. Only a refresh under the lock can notice.</summary>
public sealed record ProbeStaleActor(Guid ActorId);

public sealed class ProbeStaleActorHandler
{
    private readonly ISession m_session;
    private readonly AtlasDatabase m_database;
    private readonly IContentAccess m_access;

    public ProbeStaleActorHandler(ISession session, AtlasDatabase database, IContentAccess access)
    {
        m_session = session;
        m_database = database;
        m_access = access;
    }

    public async Task<Guid> Handle(ProbeStaleActor message, CancellationToken ct)
    {
        var cached = await m_session.GetAsync<UserAccount>(message.ActorId, ct);
        using (var other = m_database.OpenSession())
        using (var transaction = other.BeginTransaction())
        {
            await other.CreateSQLQuery("UPDATE \"UserAccount\" SET \"SecurityStamp\"='rotated' WHERE \"Id\"=:id").SetParameter("id", message.ActorId).ExecuteUpdateAsync(ct);
            await transaction.CommitAsync(ct);
        }
        var locked = await m_access.LockActorAsync(null, ct);
        return locked.Id;
    }
}
