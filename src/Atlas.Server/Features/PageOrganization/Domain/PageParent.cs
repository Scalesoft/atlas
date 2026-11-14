using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Domain;

public static class PageParent
{
    /// <summary>Where a moved or restored page may go. The space root is a space operation and needs Writer on the space, as
    /// creating a root page does (D12). A target parent must be in the page's space, outside the trash and editable by the
    /// actor, because placing a page under it changes its children: a parent the actor cannot read is indistinguishable from a
    /// missing one (400), a readable parent without EditContent is refused with 403.</summary>
    public static async Task ValidateAsync(ISession session, IContentAccess access, Page page, Guid? parentId, CancellationToken ct)
    {
        await ValidateNodeAsync(session, access, page.SpaceId, parentId, ct);
        if (parentId is { } id && await PageSubtree.IsAncestorAsync(session, id, page.Id, ct))
        {
            throw new ApiException(400, "Stránku nelze přesunout pod sebe ani pod vlastní podstránku.");
        }
    }

    /// <summary>The same rules for a node of another type (a board, IContentTreeParticipant): a leaf, so it has no cycle to refuse.</summary>
    public static async Task ValidateNodeAsync(ISession session, IContentAccess access, Guid spaceId, Guid? parentId, CancellationToken ct)
    {
        if (parentId is not { } id)
        {
            await access.RequireSpaceAsync(spaceId, SpaceRole.Writer, ct);
            return;
        }
        var parent = await session.GetAsync<Page>(id, ct);
        if (parent is null || parent.SpaceId != spaceId || parent.IsTrashed || parent.IsTemplate)
        {
            throw Unavailable();
        }
        var authorization = await access.AuthorizePageAsync(parent, AccessOperation.EditContent, ct);
        if (authorization.Outcome == PageAccessOutcome.Missing)
        {
            throw Unavailable();
        }
        if (authorization.Outcome == PageAccessOutcome.Forbidden)
        {
            throw new ApiException(403, "Pod cílovou stránku nemůžete vkládat podstránky, protože nemáte oprávnění k jejím úpravám.");
        }
    }

    /// <summary>Where a node of another type is taken from: taking it from under a page changes that page's children as placing it there
    /// does, so the current parent needs EditContent too (SEC-Z-3). A parent the actor cannot read hides the node, which is then
    /// <paramref name="missing"/>; a readable parent without EditContent is refused with 403.</summary>
    public static async Task RequireCurrentAsync(ISession session, IContentAccess access, Guid? parentId, ApiException missing, CancellationToken ct)
    {
        if (parentId is not { } id || await session.GetAsync<Page>(id, ct) is not { } parent)
        {
            return;
        }
        var authorization = await access.AuthorizePageAsync(parent, AccessOperation.EditContent, ct);
        if (authorization.Outcome == PageAccessOutcome.Missing)
        {
            throw missing;
        }
        if (authorization.Outcome == PageAccessOutcome.Forbidden)
        {
            throw new ApiException(403, "Pod touto stránkou nemůžete nic přesouvat ani odebírat, protože nemáte oprávnění k jejím úpravám.");
        }
    }

    private static ApiException Unavailable()
    {
        return new ApiException(400, "Cílová rodičovská stránka není dostupná v tomto prostoru.");
    }
}
