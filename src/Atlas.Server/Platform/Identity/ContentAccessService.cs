using NHibernate;
using Atlas.Server.Features.PageOrganization.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Identity;

public sealed class ContentAccessService : IContentAccess
{
    private readonly ISession m_session;
    private readonly ICurrentActor m_currentActor;
    private readonly IAtlasLocks m_locks;
    private readonly SpaceRoles m_roles;
    private readonly ResourceGrantSnapshots m_grants;
    private readonly IRequestChannel m_channel;
    // Per transaction: the spaces locked FOR SHARE and their grant snapshots (account + space defaults), stable under those locks.
    private readonly HashSet<Guid> m_lockedSpaces = [];
    private readonly Dictionary<Guid, ResourceGrantSnapshot> m_spaceGrants = [];
    private ITransaction? m_transaction;
    private UserAccount? m_lockedActor;
    // Per transaction: the locked account's administrator sources, read once for every decision under its lock.
    private WikiAdminSources? m_wikiAdmin;

    public ContentAccessService(ISession session, ICurrentActor currentActor, IAtlasLocks locks, SpaceRoles roles, ResourceGrantSnapshots grants,
        IRequestChannel channel)
    {
        m_channel = channel;
        m_session = session;
        m_currentActor = currentActor;
        m_locks = locks;
        m_roles = roles;
        m_grants = grants;
    }

    public Task<UserAccount> LockActorAsync(Guid? spaceId, CancellationToken ct)
    {
        return LockActorInSpacesAsync(spaceId is { } space ? [space] : [], ct);
    }

    public async Task<UserAccount> LockActorInSpacesAsync(IReadOnlyCollection<Guid> spaceIds, CancellationToken ct)
    {
        // The claims name the account; it is read once, under its lock, and the stamp is compared there.
        var claimed = m_currentActor.Claimed();
        await m_locks.AcquireContentSharedAsync(ct);
        EnterTransaction();
        var spaces = spaceIds.Distinct().Order().ToArray();
        foreach (var space in spaces)
        {
            await m_locks.LockSpaceSharedAsync(space, ct);
        }
        // Locked and checked earlier in this transaction: its FOR SHARE holds until the end, and whatever changes the account or revokes
        // its session takes it FOR UPDATE, so neither the read nor the revocation check can answer differently now.
        if (m_lockedActor is { } locked && locked.Id == claimed.Id && locked.IsActive && locked.SecurityStamp == claimed.SecurityStamp)
        {
            m_lockedSpaces.UnionWith(spaces);
            return locked;
        }
        var current = await m_locks.ReadAccountSharedAsync(claimed.Id, ct);
        if (current is null || !current.IsActive || current.SecurityStamp != claimed.SecurityStamp)
        {
            throw new ApiException(401, "Přihlášení vypršelo.");
        }
        await m_locks.SetActorAsync(current.Id, ct, claimed.BrowserSessionId);
        m_lockedActor = current;
        // Row locks last until the transaction ends, so every space locked in this transaction stays locked.
        m_lockedSpaces.UnionWith(spaces);
        return current;
    }

    public async Task<SpaceRole> RequireSpaceAsync(Guid spaceId, SpaceRole required, CancellationToken ct)
    {
        // Authorization uses the account re-read under locks, never the request-time claims or a cached instance.
        var actor = m_lockedActor ?? throw new InvalidOperationException("Call LockActorAsync before RequireSpaceAsync.");
        if (!await LiveSpace.ExistsAsync(m_session, spaceId, ct))
        {
            throw new ApiException(404, "Prostor nebyl nalezen.");
        }
        var role = await HiddenFromAiAsync(spaceId, ct) ? null : await IsWikiAdminAsync(actor, ct) ? SpaceRole.Admin : await m_roles.EffectiveAsync(actor, spaceId, ct);
        if (!ResourceAccessPolicy.AllowsLegacyContent(false, role, required))
        {
            // Who reads the space knows it exists, so it is told the missing right (review S3); anyone else learns nothing.
            throw ResourceAccessPolicy.AllowsLegacyContent(false, role, SpaceRole.Reader)
                ? new ApiException(403, required == SpaceRole.Admin
                    ? "K této operaci potřebujete být správcem prostoru."
                    : "K úpravám obsahu potřebujete v prostoru oprávnění k zápisu.")
                : new ApiException(404, "Prostor nebyl nalezen nebo k němu nemáte přístup.");
        }
        return role!.Value;
    }

    public async Task<PageAuthorization> AuthorizePageAsync(Page page, AccessOperation operation, CancellationToken ct)
    {
        return (await AuthorizePagesAsync([page], operation, ct))[0];
    }

    public async Task<IReadOnlyList<PageAuthorization>> AuthorizePagesAsync(IReadOnlyList<Page> pages, AccessOperation operation, CancellationToken ct)
    {
        var actor = m_lockedActor ?? throw new InvalidOperationException("Call LockActorAsync before a page decision.");
        if (m_lockedSpaces.Count == 0)
        {
            throw new InvalidOperationException("Call LockActorAsync with the page's space before a page decision.");
        }
        if (m_transaction is not { IsActive: true } || !ReferenceEquals(m_session.GetCurrentTransaction(), m_transaction))
        {
            throw new InvalidOperationException("The locks of LockActorAsync ended with their transaction; lock again in this transaction.");
        }
        if (pages.Any(page => !m_lockedSpaces.Contains(page.SpaceId)))
        {
            throw new ApiException(409, "Stránka byla přesunuta. Načtěte ji znovu.", "space_changed");
        }
        var now = DateTimeOffset.UtcNow;
        var wikiAdmin = await IsWikiAdminAsync(actor, ct);
        var result = new PageAuthorization[pages.Count];
        foreach (var space in pages.Select(page => page.SpaceId).Distinct())
        {
            // The private draft rule needs no grant, so an invisible page never costs a query; a space hidden from AI shows none (G5).
            var visible = await HiddenFromAiAsync(space, ct) ? []
                : pages.Where(page => page.SpaceId == space && PageVisibility.IsVisible(page, actor.Id, wikiAdmin)).Select(page => page.Id).ToArray();
            var snapshot = visible.Length == 0 ? null : (await SpaceGrantsAsync(actor, space, ct)).With(await m_grants.PageGrantsAsync(actor, space, visible, ct));
            for (var index = 0; index < pages.Count; index++)
            {
                if (pages[index].SpaceId == space)
                {
                    result[index] = snapshot?.Authorize(pages[index], operation, now) ?? PageAuthorization.Missing(operation);
                }
            }
        }
        return result;
    }

    public async Task<PageAuthorization> RequirePageAsync(Page page, AccessOperation operation, CancellationToken ct)
    {
        return (await AuthorizePageAsync(page, operation, ct)).Require();
    }

    /// <summary>G5 (D10): on a channel whose content may go to a model, a space excluded from AI and MCP is as missing as one the actor
    /// may not read, for everyone, a WikiAdmin too. The web session never asks.</summary>
    private async Task<bool> HiddenFromAiAsync(Guid spaceId, CancellationToken ct) => m_channel.Current.IsAiBound && !await m_roles.InAiAsync(spaceId, ct);

    /// <summary>The account and space defaults of a space locked in this transaction; its FOR SHARE lock keeps them stable.</summary>
    private async Task<ResourceGrantSnapshot> SpaceGrantsAsync(UserAccount actor, Guid spaceId, CancellationToken ct)
    {
        if (!m_spaceGrants.TryGetValue(spaceId, out var snapshot))
        {
            snapshot = await m_grants.ForSpaceAsync(actor, spaceId, ct, await WikiAdminAsync(actor, ct));
            m_spaceGrants[spaceId] = snapshot;
        }
        return snapshot;
    }

    private async Task<bool> IsWikiAdminAsync(UserAccount actor, CancellationToken ct) =>
        (await WikiAdminAsync(actor, ct)).Authority(DateTime.UtcNow).Allowed;

    /// <summary>The administrator sources (EffectiveWikiAdmin) of the account locked in this transaction, read once in it.</summary>
    private async Task<WikiAdminSources> WikiAdminAsync(UserAccount actor, CancellationToken ct) =>
        m_wikiAdmin ??= await EffectiveWikiAdmin.ReadAsync(m_session, actor, ct);

    /// <summary>Locks and the grants they protect belong to one transaction; a new transaction starts with none.</summary>
    private void EnterTransaction()
    {
        var current = m_session.GetCurrentTransaction();
        if (!ReferenceEquals(current, m_transaction))
        {
            m_transaction = current;
            m_lockedSpaces.Clear();
            m_spaceGrants.Clear();
            m_lockedActor = null;
            m_wikiAdmin = null;
        }
    }
}
