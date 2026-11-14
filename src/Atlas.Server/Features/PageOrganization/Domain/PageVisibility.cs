namespace Atlas.Server.Features.PageOrganization.Domain;

public static class PageVisibility
{
    /// <summary>A page is visible once published; before that only its owner and a wiki administrator may see it.</summary>
    public static bool IsVisible(Page page, Guid actorId, bool isWikiAdmin)
    {
        return page.PublishedRevisionId is not null || page.OwnerId == actorId || isWikiAdmin;
    }
}
