using System.Text.Json.Nodes;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Objects.Domain;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Objects.Commands;

/// <summary>
/// One change of an object the platform stores (F1 plan §2.3), under the envelope FOR UPDATE, so the changes of an object
/// serialize and its versions have no gaps. A retried change id with the same payload answers its original version (before the
/// expected version is compared: the retry of a change that went through expects the version before it); with another payload it
/// is refused. The type's strategy (<see cref="IObjectChangeHandler"/>) validates and applies; pages, boards and every type of
/// another editing model keep their own paths (400 unsupported_change). The object's view sessions hear of it after the commit.
/// </summary>
public sealed class ApplyObjectChangeHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly ObjectTypes m_types;
    private readonly AccessChanges m_changes;
    private readonly ViewSessions m_views;
    private readonly Outbox m_outbox;

    public ApplyObjectChangeHandler(ISession session, IContentAccess access, ObjectTypes types, AccessChanges changes, ViewSessions views, Outbox outbox)
    {
        m_outbox = outbox;
        m_session = session;
        m_access = access;
        m_types = types;
        m_changes = changes;
        m_views = views;
    }

    public async Task<ObjectChangeResult> Handle(ApplyObjectChange command, CancellationToken ct)
    {
        if (command.ChangeId is not { } changeId || changeId == Guid.Empty || command.Payload is not { } payload)
        {
            throw ObjectErrors.InvalidChange();
        }
        var json = ObjectEnvelopes.Serialize(payload);
        // An object never changes its space, so its scope may be read before the content lock.
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
        if (type is not IObjectChangeHandler changes)
        {
            throw ObjectErrors.UnsupportedChange();
        }

        var previous = await m_session.QueryOver<ObjectChange>().Where(x => x.ObjectId == envelope.Id && x.ChangeId == changeId).SingleOrDefaultAsync(ct);
        if (previous is not null)
        {
            return JsonNode.DeepEquals(JsonNode.Parse(previous.PayloadJson), payload) ? new ObjectChangeResult(previous.Version, true) : throw ObjectErrors.OperationConflict();
        }
        if (command.ExpectedVersion is { } expected && expected != envelope.Version)
        {
            throw ObjectErrors.VersionConflict();
        }
        ObjectWrite write;
        try
        {
            var data = envelope.Data is { } stored ? JsonNode.Parse(stored)!.AsObject() : null;
            write = await changes.ApplyAsync(new ObjectChangeContext(envelope.Id, new ObjectActor(actor), data, envelope.Scope, envelope.SpaceId), payload, ct);
        }
        catch (ObjectChangeRejected rejected)
        {
            throw ObjectErrors.InvalidChange(rejected.Message);
        }

        var now = DateTime.UtcNow;
        envelope.Version++;
        envelope.UpdatedAt = now;
        envelope.Title = ObjectEnvelopes.Title(write.Title);
        envelope.Data = write.Data is { } next ? ObjectEnvelopes.Serialize(next) : envelope.Data;
        m_outbox.ObjectRevised(envelope.Id, envelope.TypeId);
        await m_session.SaveAsync(new ObjectChange
        {
            ObjectId = envelope.Id, Version = envelope.Version, ChangeId = changeId, AuthorId = actor.Id, AppliedAt = now, PayloadJson = json
        }, ct);
        var message = new ObjectChangedMessage(envelope.Id, envelope.Version, write.Change);
        m_changes.AfterCommit(() => m_views.ObjectChangedAsync(message));
        return new ObjectChangeResult(envelope.Version, false);
    }
}
