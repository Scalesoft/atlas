namespace Atlas.Server.Features.KnowledgeMaintenance;

public sealed record GetDuplicatePages(Guid SpaceId);
public sealed record DismissDuplicatePages(Guid SpaceId, Guid PageId, Guid OtherPageId);
public sealed record DuplicatePagePair(Guid PageId, string Title, Guid OtherPageId, string OtherTitle, double Similarity);
public sealed record DuplicatePageList(IReadOnlyList<DuplicatePagePair> Items);

public sealed class GetDuplicatePagesHandler(IContentAccess access, DuplicatePageStore store)
{
    public async Task<DuplicatePageList> Handle(GetDuplicatePages query, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(query.SpaceId, ct);
        await access.RequireSpaceAsync(query.SpaceId, SpaceRole.Admin, ct);
        return new DuplicatePageList(await store.ListAsync(actor, query.SpaceId, ct));
    }
}

public sealed class DismissDuplicatePagesHandler(IContentAccess access, IAtlasLocks locks, DuplicatePageStore store, NHibernate.ISession session)
{
    public async Task<bool> Handle(DismissDuplicatePages command, CancellationToken ct)
    {
        await locks.AcquireContentSharedAsync(ct);
        await access.LockActorAsync(command.SpaceId, ct);
        await access.RequireSpaceAsync(command.SpaceId, SpaceRole.Admin, ct);
        // Space inclusion can be revoked under the space lock held by LockActorAsync.
        if (!await session.CreateSQLQuery($"SELECT {AccessSql.SpaceInAi(":space")}").SetParameter("space", command.SpaceId).UniqueResultAsync<bool>(ct))
        {
            throw new ApiException(404, "Stránka nebyla nalezena.");
        }
        if (command.PageId == command.OtherPageId)
        {
            throw new ApiException(400, "Vyberte dvě různé stránky.");
        }
        // Stable lock ordering for two concurrent decisions in opposite pair orders.
        foreach (var id in new[] { command.PageId, command.OtherPageId }.Order())
        {
            var page = await locks.ReadPageSharedAsync(id, ct);
            if (page is null || page.SpaceId != command.SpaceId || page.IsTrashed || page.IsTemplate || page.PublishedRevisionId is null)
            {
                throw new ApiException(404, "Stránka nebyla nalezena.");
            }
            await access.RequirePageAsync(page, AccessOperation.ReadContent, ct);
        }
        await store.DismissAsync(command.PageId, command.OtherPageId, ct);
        return true;
    }
}
