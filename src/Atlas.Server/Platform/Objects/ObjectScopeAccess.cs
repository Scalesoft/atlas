using Atlas.Server.Features.Spaces.Domain;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Objects;

/// <summary>
/// Access by the envelope's scope alone (objects spec §2.3), for the types whose objects the platform stores: the wiki is read by
/// every active account and changed by a WikiAdmin (a type that lets more write it, IWikiWriters, widens that itself); a space object follows the space role (Reader reads, Writer writes, a
/// WikiAdmin is Admin everywhere) while the space is outside the trash (LiveSpace); a private object is its owner's and its grantees' (ObjectGrants), no
/// administrator's. Trashed or missing is None. The library lists a subset of the same rules (ListObjectsHandler: a private object only to its owner; its grantees
/// find it in Sdíleno se mnou), so a listed object is always one this grants.
/// </summary>
public sealed class ObjectScopeAccess
{
    private readonly ISession m_session;
    private readonly SpaceRoles m_roles;
    private readonly IRequestChannel m_channel;

    public ObjectScopeAccess(ISession session, SpaceRoles roles, IRequestChannel channel)
    {
        m_session = session;
        m_roles = roles;
        m_channel = channel;
    }

    public async Task<ObjectAccess> AuthorizeAsync(ObjectActor actor, Guid objectId, CancellationToken ct)
    {
        var account = actor.Account;
        if (!account.IsActive || await m_session.GetAsync<ContentResource>(objectId, ct) is not { TrashedAt: null } envelope)
        {
            return ObjectAccess.None;
        }
        switch (envelope.Scope)
        {
            case ObjectScopes.Private:
                // No automatic access for any administrator (spec 2026-10-29 R3): the owner, or a grant on this one object.
                return envelope.OwnerId == account.Id ? ObjectAccess.Write : await ObjectGrants.AccessAsync(m_session, account.Id, envelope.Id, ct);
            case ObjectScopes.Wiki:
                return await m_roles.IsWikiAdminAsync(account, ct) ? ObjectAccess.Write : ObjectAccess.Read;
            case ObjectScopes.Space when envelope.SpaceId is { } spaceId:
                // G5 (D10): a space excluded from AI and MCP is as missing on a channel whose content may go to a model, as IContentAccess.
                if (!await LiveSpace.ExistsAsync(m_session, spaceId, ct) || m_channel.Current.IsAiBound && !await m_roles.InAiAsync(spaceId, ct))
                {
                    return ObjectAccess.None;
                }
                var role = await m_roles.IsWikiAdminAsync(account, ct) ? SpaceRole.Admin : await m_roles.EffectiveAsync(account, spaceId, ct);
                return role switch
                {
                    null => ObjectAccess.None,
                    >= SpaceRole.Writer => ObjectAccess.Write,
                    _ => ObjectAccess.Read
                };
            default:
                return ObjectAccess.None;
        }
    }

    /// <summary>Whether the account may create and change wiki objects of the type: a WikiAdmin, or whom the type names
    /// (<see cref="IWikiWriters"/>).</summary>
    public async Task<bool> MayWriteWikiAsync(ObjectActor actor, IObjectType type, CancellationToken ct)
    {
        return type is IWikiWriters writers ? await writers.MayWriteWikiAsync(actor, ct) : await m_roles.IsWikiAdminAsync(actor.Account, ct);
    }

    /// <summary>Who lists, restores and permanently deletes a trashed object (unified trash spec §2, rule 3): the owner of a private one,
    /// who writes the type's wiki objects of a wiki one, an Admin of its space (outside the trash) of a space one. ListObjectTrashHandler
    /// filters by the same rules.</summary>
    public async Task<bool> MayManageTrashAsync(ObjectActor actor, ContentResource envelope, IObjectType type, CancellationToken ct)
    {
        var account = actor.Account;
        return account.IsActive && envelope.Scope switch
        {
            ObjectScopes.Private => envelope.OwnerId == account.Id,
            ObjectScopes.Wiki => await MayWriteWikiAsync(actor, type, ct),
            ObjectScopes.Space when envelope.SpaceId is { } spaceId => await LiveSpace.ExistsAsync(m_session, spaceId, ct)
                && (await m_roles.IsWikiAdminAsync(account, ct) || await m_roles.EffectiveAsync(account, spaceId, ct) >= SpaceRole.Admin),
            _ => false
        };
    }

    /// <summary>A WikiAdmin, or an account that writes in at least one space.</summary>
    public async Task<bool> IsWikiAdminOrSpaceWriterAsync(ObjectActor actor, CancellationToken ct)
    {
        return actor.Account.IsActive && (await m_roles.IsWikiAdminAsync(actor.Account, ct)
            || (await m_roles.EffectiveAsync(actor.Account, ct)).Values.Any(role => role >= SpaceRole.Writer));
    }
}
