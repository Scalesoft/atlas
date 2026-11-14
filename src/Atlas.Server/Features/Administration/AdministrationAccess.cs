using NHibernate;
using Atlas.Server.Features.Administration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration;

/// <summary>The lock and authorization ladder every administration message runs first. Same order and the same messages as the ADO.NET slice it replaces.</summary>
public sealed class AdministrationAccess
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly SpaceRoles m_roles;

    public AdministrationAccess(ISession session, IContentAccess access, IAtlasLocks locks, SpaceRoles roles)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_roles = roles;
    }

    /// <summary>Reads: lock the actor row, then require the wiki administrator role on the locked account.</summary>
    public async Task<UserAccount> RequireWikiAdminAsync(CancellationToken ct)
        => await m_roles.RequireWikiAdminAsync(await m_access.LockActorAsync(null, ct), ct);

    /// <summary>Writes: serialize the whole administration surface first, then validate the actor.</summary>
    public async Task<UserAccount> LockAdministrationAsync(CancellationToken ct)
    {
        await m_locks.AcquireContentExclusiveAsync(ct);
        return await RequireWikiAdminAsync(ct);
    }

    public Task<UserAccount> LockActorAsync(CancellationToken ct)
    {
        return m_access.LockActorAsync(null, ct);
    }

    /// <summary>Space access administration: the space exists (locked for writes), the actor is current, and holds Admin on that space.</summary>
    public async Task<UserAccount> RequireSpaceAdminAsync(Guid spaceId, bool mutate, CancellationToken ct)
    {
        // The content lock comes before any row lock, or this deadlocks against the writes that take it first.
        // LockActorAsync re-acquiring the same shared advisory lock below is a no-op.
        await m_locks.AcquireContentSharedAsync(ct);
        var space = await LiveSpace.FindAsync(m_session, spaceId, ct, mutate ? LockMode.Upgrade : LockMode.None);
        if (space is null)
        {
            throw new ApiException(404, "Prostor nebyl nalezen.");
        }
        var actor = await m_access.LockActorAsync(null, ct);
        // Deliberately not IContentAccess.RequireSpaceAsync: this feature answers "missing" and "not an admin"
        // with different statuses and different messages, and the wire contract freezes both. The grant query
        // itself is shared — SpaceRoles is the only place that reads SpaceGrant for an effective role.
        var role = await m_roles.IsWikiAdminAsync(actor, ct) ? SpaceRole.Admin : await m_roles.EffectiveAsync(actor, spaceId, ct);
        if (role is null)
        {
            throw new ApiException(404, "Prostor nebyl nalezen.");
        }
        if (role < SpaceRole.Admin)
        {
            throw new ApiException(403, "Přístupy může měnit pouze správce tohoto prostoru.");
        }
        return actor;
    }
}
