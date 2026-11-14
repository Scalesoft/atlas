using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.YouTrack;

/// <summary>Effective space role without content locks. Identical rules and messages to IContentAccess.RequireSpaceAsync, but without the shared content advisory lock. It was written for a read path that held its transaction across an upstream HTTP call; it stays lock-free now that the call runs between two short transactions, so a read does not queue behind an exclusive content writer such as the settings save. The YouTrack search and resolve reads (YouTrackReadRunner) call it in their first transaction and again in the second, before they release an answer (after the call or a cache hit); a space without the integration and a failed call end after the first. The connection test has no space and does not call it.</summary>
public sealed class YouTrackSpaceAccess
{
    private readonly ISession m_session;
    private readonly SpaceRoles m_roles;

    public YouTrackSpaceAccess(ISession session, SpaceRoles roles)
    {
        m_session = session;
        m_roles = roles;
    }

    public async Task<SpaceRole> RequireAsync(UserAccount actor, Guid spaceId, SpaceRole required, CancellationToken ct)
    {
        if (!await LiveSpace.ExistsAsync(m_session, spaceId, ct))
        {
            throw new ApiException(404, "Prostor nebyl nalezen.");
        }
        if (await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            return SpaceRole.Admin;
        }
        // Reuses SpaceRoles.EffectiveAsync (the same grant/group resolution ContentAccessService.RequireSpaceAsync
        // uses) instead of duplicating the chunked group-membership query.
        var role = await m_roles.EffectiveAsync(actor, spaceId, ct);
        if (!ResourceAccessPolicy.AllowsLegacyContent(false, role, required))
        {
            throw new ApiException(404, "Prostor nebyl nalezen nebo k němu nemáte přístup.");
        }
        return role!.Value;
    }
}
