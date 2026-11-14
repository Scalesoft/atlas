using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SpaceCovers.Commands;

internal static class SpaceCoverChanges
{
    /// <summary>The rule and lock order of saving the space settings: the shared content lock, the space FOR UPDATE (one change of
    /// its image at a time), the actor; only the space Admin or a WikiAdmin passes, a member gets 403, anyone else the space 404. Hands back
    /// the locked space too: the answer computes the automatic image from its name, description and icon.</summary>
    public static async Task<(UserAccount Actor, Space Space)> LockAsync(ISession session, IContentAccess access, IAtlasLocks locks, Guid spaceId, CancellationToken ct)
    {
        await locks.AcquireContentSharedAsync(ct);
        var space = await LiveSpace.FindAsync(session, spaceId, ct, LockMode.Upgrade)
            ?? throw new ApiException(404, "Prostor nebyl nalezen nebo k němu nemáte přístup.");
        var actor = await access.LockActorAsync(null, ct);
        await access.RequireSpaceAsync(spaceId, SpaceRole.Admin, ct);
        return (actor, space);
    }

    public static ApiException Missing() => new(404, "Prostor nemá úvodní obrázek.");
}
