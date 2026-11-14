using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageAccess.Queries;

public sealed record GetPageAccess(Guid PageId, int Offset = 0, int Limit = 50, string? Query = null);

public sealed class PageAccessHandler(ISession session, PageReader reader, IContentAccess access)
{
    public async Task<PageAccessState> Handle(GetPageAccess query, CancellationToken ct)
    {
        // Keep the same current-identity, placement and private-draft checks as the content endpoint.
        // The page read authorizes ReadContent; everyone who can read the page gets their own explanation.
        var visible = await reader.ReadAsync(query.PageId, ct);
        var actor = await access.LockActorAsync(visible.SpaceId, ct);
        if (query.Offset < 0 || query.Limit is < 1 or > 100 || query.Query?.Length > 200)
            throw new ApiException(400, "Počet výsledků musí být 1–100, posun nezáporný a hledání nejvýše 200 znaků.");

        var page = await session.GetAsync<Page>(query.PageId, ct);
        var space = await session.GetAsync<Space>(visible.SpaceId, ct);
        var own = (await PageAccessDirectory.PersonAsync(session, page, actor.Id, ct))!;
        var canManage = await CanManageAsync(page, ct);
        if (!canManage)
            return new(visible.SpaceId, space.Name, visible.IsPublished, false, own, [], false, visible.Title);

        // Management detail (WP3 S5): each part is one statement over the sources of PageAccessDirectory, no per-person queries.
        var term = query.Query?.Trim() ?? "";
        var (items, hasMore) = await PageAccessDirectory.PeopleAsync(session, page, term, query.Offset, query.Limit, ct);
        var modeVersion = await Commands.SetPageAccessModeHandler.CurrentVersionAsync(session, page.Id, ct);
        return new(visible.SpaceId, space.Name, visible.IsPublished, true, own, items, hasMore, visible.Title,
            page.OwnAccess ? "custom" : "inherit", modeVersion is { } version ? AdministrationVersion.Format(version) : null,
            await PageAccessDirectory.CountsAsync(session, page, ct),
            query.Offset == 0 ? await PageAccessDirectory.GroupsAsync(session, page, term, ct) : [],
            await PageAccessDirectory.ShareLinksAsync(session, page, ct));
    }

    /// <summary>The list of people with access is for those who may manage access to the page.</summary>
    private async Task<bool> CanManageAsync(Page page, CancellationToken ct)
    {
        return (await access.AuthorizePageAsync(page, AccessOperation.ManageAccess, ct)).Allowed;
    }
}
