using Atlas.Server.Features.Administration;
using Atlas.Server.Features.ExternalSharing.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ExternalSharing;

/// <summary>All management and public reads use the same content/space/account/page lock order.</summary>
public sealed class ExternalSharingAccess(ISession session, IAtlasLocks locks, AdministrationAccess administration, IContentAccess content,
    ResourceGrantSnapshots grants, AtlasDatabase database)
{
    public async Task<UserAccount> RequireSpaceAdminAsync(Guid spaceId, bool mutate, CancellationToken ct)
    {
        if (mutate) await locks.AcquireContentExclusiveAsync(ct);
        else
        {
            await locks.AcquireContentSharedAsync(ct);
            await locks.LockSpaceSharedAsync(spaceId, ct);
        }
        return await administration.RequireSpaceAdminAsync(spaceId, mutate, ct);
    }

    /// <summary>Share links of one page: Share on that page (space Admin, WikiAdmin or a page DocumentManager). A reader is told the refusal (403).</summary>
    public async Task<(Page Page, UserAccount Actor)> RequirePageShareAsync(Guid pageId, bool mutate, CancellationToken ct)
    {
        if (mutate) await locks.AcquireContentExclusiveAsync(ct);
        else await locks.AcquireContentSharedAsync(ct);
        var page = await session.GetAsync<Page>(pageId, ct) ?? throw MissingPage();
        var originalSpace = page.SpaceId;
        if (mutate && !await locks.LockSpaceForUpdateAsync(originalSpace, ct)) throw MissingPage();
        var actor = await content.LockActorAsync(originalSpace, ct);
        await locks.LockPageSharedAsync(pageId, ct);
        await session.RefreshAsync(page, ct);
        if (page.SpaceId != originalSpace) throw MissingPage();
        await content.RequirePageAsync(page, AccessOperation.Share, ct);
        return (page, actor);
    }

    public async Task<PageShareLink> RequirePublicAsync(string tokenHash, CancellationToken ct)
    {
        await locks.AcquireContentSharedAsync(ct);
        var link = await session.QueryOver<PageShareLink>().Where(x => x.TokenHash == tokenHash).SingleOrDefaultAsync(ct);
        if (link is null) throw Missing();
        await locks.LockSpaceSharedAsync(link.SpaceId, ct);
        await locks.LockAccountSharedAsync(link.CreatedById, ct);
        await locks.LockPageSharedAsync(link.PageId, ct);
        // Metadata loaded before row locks may have changed while a grant or page writer committed.
        await session.RefreshAsync(link, ct);
        var page = await session.GetAsync<Page>(link.PageId, ct);
        var creator = await session.GetAsync<UserAccount>(link.CreatedById, ct);
        var policy = await session.GetAsync<SpaceExternalSharingPolicy>(link.PolicyId, ct);
        if (page is not null) await session.RefreshAsync(page, ct);
        if (creator is not null) await session.RefreshAsync(creator, ct);
        if (policy is not null) await session.RefreshAsync(policy, ct);
        if (!IsAvailable(link, policy, page, DateTime.UtcNow)
            || creator is not { IsActive: true } || creator.SecurityStamp != link.CreatorSecurityStamp)
            throw Missing();
        if (!await CanShareUntilAsync(creator, page!, link.ExpiresAt, ct))
        {
            await CloseLapsedAsync(link.Id, ct);
            throw Missing();
        }
        return link;
    }

    /// <summary>The issuer's authority ran out with nothing that closes links (a grant or a Hub verification expired): the first refused
    /// open closes the link and records share.revoked without an actor, in a transaction of its own, as the refusal rolls back this one.
    /// A later open finds it closed and records nothing.</summary>
    private async Task CloseLapsedAsync(Guid linkId, CancellationToken ct)
    {
        using var own = database.OpenSession();
        using var transaction = own.BeginTransaction();
        await ShareRevocation.LinkAsync(own, linkId, null, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>The issuer holds Share on the page for the whole life of the link: WikiAdmin, or a grant allowing Share
    /// (space Admin or page DocumentManager) that is unbounded or expires no earlier than the link.</summary>
    public async Task<bool> CanShareUntilAsync(UserAccount actor, Page page, DateTime expiry, CancellationToken ct)
    {
        var snapshot = await grants.ForPageAsync(actor, page, ct);
        var authority = snapshot.Authority(page, AccessOperation.Share, DateTimeOffset.UtcNow);
        return authority.Allowed && (authority.Until is null || expiry <= authority.Until);
    }

    private static bool IsAvailable(PageShareLink link, SpaceExternalSharingPolicy? policy, Page? page, DateTime now)
        => link.ActiveTo is null && link.ExpiresAt > now
           && policy is { Enabled: true, ActiveTo: null } && policy.SpaceId == link.SpaceId
           && page is not null && !page.IsTrashed && page.SpaceId == link.SpaceId && page.PublishedRevisionId is not null;

    /// <summary>The revision a link shows: the one it pinned, or for a live page (3.6-I Živě) the latest saved one, as its readers read it,
    /// when the link was created on a live page (a link to a Manual page never starts following autosaves).</summary>
    public async Task<Guid> SharedRevisionAsync(PageShareLink link, CancellationToken ct)
    {
        if (!link.FollowsLive) return link.RevisionId;
        var page = await session.GetAsync<Page>(link.PageId, ct);
        // The space is held FOR SHARE by the link's read, which already read its mode: no second statement for the space.
        if (page is null || (page.PublicationMode ?? locks.SharedSpacePublicationMode(page.SpaceId) ?? await PublicationModes.EffectiveAsync(session, page, ct))
                != PublicationMode.Live)
            return link.RevisionId;
        return await session.QueryOver<PageRevision>().Where(x => x.PageId == link.PageId && x.ActiveTo == null).Select(x => x.Id)
            .SingleOrDefaultAsync<Guid>(ct);
    }

    public static ApiException Missing() => new(404, "Sdílená stránka není dostupná.");
    private static ApiException MissingPage() => new(404, "Stránka nebyla nalezena.");
}
