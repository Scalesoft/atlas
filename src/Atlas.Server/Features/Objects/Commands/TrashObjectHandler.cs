using Atlas.Server.Features.Objects.Domain;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Objects.Commands;

/// <summary>
/// Moves an object the platform stores to the trash (its envelope's TrashedAt): it is gone from every read, the library and its
/// placements at once, and who held it live is told after the commit (Revoked). Who may change it may trash it, a private object only its
/// owner; pages and boards keep their own trash (400 unsupported_change). The trash list, restore and purge are ObjectTrashHandler's.
/// </summary>
public sealed class TrashObjectHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly ObjectTypes m_types;
    private readonly AccessChanges m_changes;
    private readonly LiveRelay m_relay;

    public TrashObjectHandler(ISession session, IContentAccess access, ObjectTypes types, AccessChanges changes, LiveRelay relay)
    {
        m_session = session;
        m_access = access;
        m_types = types;
        m_changes = changes;
        m_relay = relay;
    }

    public async Task Handle(TrashObject command, CancellationToken ct)
    {
        var spaceId = await m_session.QueryOver<ContentResource>().Where(x => x.Id == command.ObjectId).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid?>(ct);
        var actor = await m_access.LockActorAsync(spaceId, ct);
        var envelope = await m_session.GetForUpdateAsync<ContentResource>(command.ObjectId, ct);
        if (envelope is not { TrashedAt: null } || m_types.Find(envelope.TypeId) is not { } type)
        {
            throw ObjectErrors.NotFound();
        }
        switch (await type.AuthorizeAsync(new ObjectActor(actor), envelope.Id, ct))
        {
            case ObjectAccess.None:
                throw ObjectErrors.NotFound();
            case ObjectAccess.Read:
                throw ObjectErrors.ReadOnly();
        }
        // A private object is shared for editing, not for removing: only its owner trashes it (spec 2026-10-29 R2).
        if (envelope.Scope == ObjectScopes.Private && envelope.OwnerId != actor.Id)
        {
            throw ObjectErrors.ReadOnly();
        }
        if (type is not IObjectChangeHandler)
        {
            throw ObjectErrors.UnsupportedChange();
        }
        envelope.TrashedAt = envelope.UpdatedAt = DateTime.UtcNow;
        envelope.TrashedById = actor.Id;
        m_changes.AfterCommit(() => m_relay.RefreshAsync(ViewSessions.Channel(envelope.Id), CancellationToken.None));
    }
}
