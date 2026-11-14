using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageAccess.Queries;

/// <summary>The page reads of the access tab, the people directory and the security centre: the page read locks the actor and the
/// page's space, refreshes the page and authorizes ReadContent (404 otherwise), then the operation (403 for a reader).</summary>
internal static class PageAccessGate
{
    public static async Task<Page> RequireAsync(PageReader reader, ISession session, IContentAccess access, Guid pageId, AccessOperation operation,
        CancellationToken ct)
    {
        var visible = await reader.ReadAsync(pageId, ct);
        var page = await session.GetAsync<Page>(visible.Id, ct) ?? throw new ApiException(404, "Stránka nebyla nalezena.");
        await access.RequirePageAsync(page, operation, ct);
        return page;
    }
}
