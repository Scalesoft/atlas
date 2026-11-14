using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Objects.Domain;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Objects.Commands;

/// <summary>
/// A new object of a type the platform stores (F1 plan §2.3). Writing into a scope needs what changing an object there needs: a
/// space Writer, a WikiAdmin for the wiki (or whom the type lets write it, IWikiWriters), anyone for a private object of their own.
/// The type validates the data; its first version is logged like every later change.
/// </summary>
public sealed class CreateObjectHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly ObjectScopeAccess m_scopes;
    private readonly ObjectTypes m_types;
    private readonly Outbox m_outbox;

    public CreateObjectHandler(ISession session, IContentAccess access, ObjectScopeAccess scopes, ObjectTypes types, Outbox outbox)
    {
        m_outbox = outbox;
        m_session = session;
        m_access = access;
        m_scopes = scopes;
        m_types = types;
    }

    public async Task<ObjectDto> Handle(CreateObject command, CancellationToken ct)
    {
        var type = m_types.Find(command.TypeId ?? "") ?? throw ObjectErrors.UnknownType();
        if (type is not IObjectChangeHandler changes)
        {
            throw ObjectErrors.UnsupportedChange();
        }
        if (!ObjectEnvelopes.IsValidScope(command.Scope, command.SpaceId))
        {
            throw ObjectErrors.InvalidObject();
        }
        var actor = await m_access.LockActorAsync(command.SpaceId, ct);
        // As for boards: no role in the space is 404, a Reader's write 403 (the space is no secret to its reader).
        if (command.SpaceId is { } spaceId && await m_access.RequireSpaceAsync(spaceId, SpaceRole.Reader, ct) < SpaceRole.Writer)
        {
            throw ObjectErrors.ReadOnly();
        }
        else if (command.Scope == ObjectScopes.Wiki && !await m_scopes.MayWriteWikiAsync(new ObjectActor(actor), type, ct))
        {
            throw ObjectErrors.ReadOnly();
        }

        var id = Guid.NewGuid();
        var data = command.Data ?? [];
        ObjectWrite write;
        try
        {
            write = await changes.CreateAsync(new ObjectChangeContext(id, new ObjectActor(actor), null, command.Scope!, command.SpaceId), data, ct);
        }
        catch (ObjectChangeRejected rejected)
        {
            throw ObjectErrors.InvalidChange(rejected.Message);
        }
        var now = DateTime.UtcNow;
        var envelope = new ContentResource
        {
            Id = id, TypeId = type.TypeId, Scope = command.Scope!, SpaceId = command.SpaceId,
            OwnerId = command.Scope == ObjectScopes.Private ? actor.Id : null, Version = 1, CreatedAt = now, CreatedBy = actor.Id, UpdatedAt = now,
            Title = ObjectEnvelopes.Title(write.Title), Data = write.Data is { } stored ? ObjectEnvelopes.Serialize(stored) : null
        };
        await m_session.SaveAsync(envelope, ct);
        m_outbox.ObjectRevised(envelope.Id, envelope.TypeId);
        await m_session.SaveAsync(new ObjectChange
        {
            ObjectId = id, Version = 1, ChangeId = id, AuthorId = actor.Id, AppliedAt = now, PayloadJson = ObjectEnvelopes.Serialize(data)
        }, ct);
        return ObjectEnvelopes.ToDto(envelope, type, true, envelope.Title);
    }
}
