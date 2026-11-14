using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalItems.Domain;

/// <summary>
/// What a personal item is (spec 2026-10-29 §1): a live private note (the owner dropped personal boards on 29. 10.). Anything else is
/// no personal item: fail closed, as the object registry is.
/// </summary>
public sealed class PersonalItemTargets(ISession session, ObjectTypes types, ObjectScopeAccess scopes)
{
    private static readonly string[] Personal = [NoteObjectType.Id];

    /// <summary>The personal types on in this process.</summary>
    public IReadOnlyList<string> TypeIds() => Personal.Where(id => types.Find(id) is not null).ToArray();

    /// <summary>The item, for its owner only: missing, trashed, not personal or unreadable is 404; a grantee who reads it learns
    /// that only its owner shares it (403). <paramref name="forUpdate"/> locks the envelope, after the caller locked the account.</summary>
    public async Task<ContentResource> RequireOwnedAsync(UserAccount actor, Guid objectId, bool forUpdate, CancellationToken ct)
    {
        var envelope = forUpdate ? await session.GetForUpdateAsync<ContentResource>(objectId, ct) : await session.GetAsync<ContentResource>(objectId, ct);
        if (envelope is not { TrashedAt: null, Scope: ObjectScopes.Private } || !TypeIds().Contains(envelope.TypeId))
        {
            throw Missing();
        }
        if (envelope.OwnerId == actor.Id)
        {
            return envelope;
        }
        throw await scopes.AuthorizeAsync(new ObjectActor(actor), objectId, ct) == ObjectAccess.None
            ? Missing()
            : new ApiException(403, "Sdílení položky spravuje jen její vlastník.");
    }

    /// <summary>The Czech name of a personal type, for the access lists.</summary>
    public static string Label(string typeId) => "Poznámka";

    private static ApiException Missing() => new(404, "Položka nebyla nalezena.");
}
