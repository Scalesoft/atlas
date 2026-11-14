using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Contacts.Domain;
using Atlas.Server.Features.Contacts.Persistence;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Objects.Domain;
using Atlas.Server.Platform.Events;
using Atlas.Server.Features.TrashCleanup.Queries;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Objects.Commands;

/// <summary>
/// The trash of an object the platform stores (unified trash spec §2): restore and permanent delete, both only of a trashed object and
/// only for who manages its trash (ObjectScopeAccess.MayManageTrashAsync); anything else is 404, as a missing object. A restore is a
/// confirmed change of its own (a new version and its ObjectChange, so the log keeps no gaps) and tells the live relay and the views at
/// once; who may read it again gets it back in the open views that show it (ViewSessions.ReadmitAsync, for every type). A purge
/// removes the envelope, its change log, comments, favorites, visits and the tree's reference nodes to it (PER3-5) and leaves an
/// ObjectPurgeEvent; boards and page mentions keep their soft references, which read as a missing object (a lock, a broken
/// mention). A type with its own tables (a page, a board: IObjectTrash) gets the whole command, so one route restores and purges every
/// type.
/// </summary>
public sealed class ObjectTrashHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly ObjectTypes m_types;
    private readonly ObjectScopeAccess m_scopes;
    private readonly AccessChanges m_changes;
    private readonly LiveRelay m_relay;
    private readonly ViewSessions m_views;
    private readonly IRequestChannel m_channel;
    private readonly McpVisibility m_visibility;
    private readonly ContactDirectory m_contacts;
    private readonly Outbox m_outbox;

    public ObjectTrashHandler(ISession session, IContentAccess access, ObjectTypes types, ObjectScopeAccess scopes, AccessChanges changes, LiveRelay relay,
        ViewSessions views, IRequestChannel channel, McpVisibility visibility, ContactDirectory contacts, Outbox outbox)
    {
        m_outbox = outbox;
        m_session = session;
        m_access = access;
        m_types = types;
        m_scopes = scopes;
        m_changes = changes;
        m_relay = relay;
        m_views = views;
        m_channel = channel;
        m_visibility = visibility;
        m_contacts = contacts;
    }

    public async Task<ObjectChangeResult> Handle(RestoreObject command, CancellationToken ct)
    {
        if (await OwnTrashAsync(command.ObjectId, ct) is { } own)
        {
            var version = await own.RestoreAsync(command.ObjectId, ct);
            m_changes.AfterCommit(() => m_views.ReadmitAsync(command.ObjectId));
            return new ObjectChangeResult(version, false);
        }
        var (envelope, actor) = await OpenTrashedAsync(command.ObjectId, ct);
        var now = DateTime.UtcNow;
        envelope.TrashedAt = null;
        envelope.TrashedById = null;
        // A merged contact comes back as itself: its redirect to the merge target ends (contacts spec §5).
        if (ContactDirectory.MergeTarget(envelope) is not null)
        {
            var data = JsonNode.Parse(envelope.Data!)!.AsObject();
            data.Remove(ContactDirectory.MergedInto);
            envelope.Data = data.ToJsonString();
            m_outbox.ObjectRevised(envelope.Id, envelope.TypeId);
        }
        await RefuseDuplicateContactAsync(envelope, actor, ct);
        envelope.Version++;
        envelope.UpdatedAt = now;
        await m_session.SaveAsync(new ObjectChange
        {
            ObjectId = envelope.Id, Version = envelope.Version, ChangeId = Guid.NewGuid(), AuthorId = actor.Id, AppliedAt = now,
            PayloadJson = new JsonObject { ["restored"] = true }.ToJsonString()
        }, ct);
        var message = new ObjectChangedMessage(envelope.Id, envelope.Version, null);
        m_changes.AfterCommit(async () =>
        {
            await m_relay.RefreshAsync(ViewSessions.Channel(message.ObjectId), CancellationToken.None);
            await m_views.ObjectChangedAsync(message);
            await m_views.ReadmitAsync(message.ObjectId);
        });
        return new ObjectChangeResult(envelope.Version, false);
    }

    public async Task Handle(PurgeObject command, CancellationToken ct)
    {
        if (await OwnTrashAsync(command.ObjectId, ct) is { } own)
        {
            await own.PurgeAsync(command.ObjectId, ct);
            return;
        }
        var (envelope, actor) = await OpenTrashedAsync(command.ObjectId, ct);
        // A merged contact holds the redirect (mergedInto) of every page, requirement and card that still names it: purged, they would lose
        // the target. It stays while any stored content names it (as the cleanup decides a use), and goes once they name the target.
        if (ContactDirectory.MergeTarget(envelope) is not null && (await TrashCleanupCandidates.MentionedAsync(m_session, [envelope.Id], ct)).Count > 0)
        {
            throw new ApiException(409, "Sloučený kontakt ještě uvádějí stránky nebo tabule a přesměrovává je na cílový kontakt. Smazat ho půjde, až je uloží s cílovým kontaktem.",
                "contact_merged_in_use");
        }
        m_session.Evict(envelope);
        await ObjectPurge.AuditAsync(m_session, envelope.Id, envelope.TypeId, envelope.Scope, envelope.SpaceId, envelope.Title, actor.Id, null, ct);
        await ObjectPurge.ForgetAsync(m_session, [envelope.Id], actor.Id, ct);
        await m_session.CreateSQLQuery("""DELETE FROM "ContentResource" WHERE "Id" = :id""").SetParameter("id", envelope.Id).ExecuteUpdateAsync(ct);
        m_changes.Changed();
    }

    /// <summary>One person once (K1) on a restore too: a merge target took the merged contact's e-mails and phones, and a new contact may have
    /// taken a trashed one's address; a contact of the wiki or a space whose strong key a live one holds stays in the trash (409).</summary>
    private async Task RefuseDuplicateContactAsync(ContentResource envelope, UserAccount actor, CancellationToken ct)
    {
        if (envelope.TypeId != ContactObjectType.Id || envelope.Scope == ObjectScopes.Private)
        {
            return;
        }
        await m_contacts.LockAsync(ct);
        if ((await m_contacts.MatchUnfilteredAsync(JsonNode.Parse(envelope.Data ?? "{}")!.AsObject(), envelope.Id, ct)).Strong is { } existing)
        {
            throw ContactErrors.RestoreDuplicate(await m_contacts.ReadableIdAsync(actor, existing.Id, ct));
        }
    }

    /// <summary>The trash of the object's type when the type keeps its own (a page, a board); null for a stored type or a missing object.
    /// A type of a disabled module is registered by none, so its objects read as missing (404).</summary>
    private async Task<IObjectTrash?> OwnTrashAsync(Guid objectId, CancellationToken ct)
    {
        var typeId = await m_session.QueryOver<ContentResource>().Where(x => x.Id == objectId).Select(x => x.TypeId).SingleOrDefaultAsync<string>(ct);
        return typeId is null ? null : m_types.Trash(typeId);
    }

    /// <summary>The trashed envelope FOR UPDATE, decided in the object path's lock order; 404 for anything the actor may not manage.</summary>
    private async Task<(ContentResource Envelope, UserAccount Actor)> OpenTrashedAsync(Guid objectId, CancellationToken ct)
    {
        var spaceId = await m_session.QueryOver<ContentResource>().Where(x => x.Id == objectId).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid?>(ct);
        var actor = await m_access.LockActorAsync(spaceId, ct);
        var envelope = await m_session.GetForUpdateAsync<ContentResource>(objectId, ct);
        if (envelope is not { TrashedAt: not null } || m_types.Find(envelope.TypeId) is not { } type || type is not IObjectChangeHandler
            || !await m_scopes.MayManageTrashAsync(new ObjectActor(actor), envelope, type, ct))
        {
            throw ObjectErrors.NotInTrash();
        }
        // G5: a space switched out of AI is missing to MCP.
        if (m_channel.Current.IsAiBound && envelope.SpaceId is { } space && (await m_visibility.ExcludedSpacesAsync([space], ct)).Count > 0)
        {
            throw ObjectErrors.NotInTrash();
        }
        return (envelope, actor);
    }
}
