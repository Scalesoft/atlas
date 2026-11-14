using NHibernate;
using NHibernate.Criterion;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Domain;

/// <summary>Tree walks that used to be recursive CTEs. Breadth-first level queries reproduce the same fixpoint, including the pruning the CTEs did in their JOIN conditions.</summary>
public static class PageSubtree
{
    /// <summary>The pages whose parent is one of <paramref name="parentIds"/>, however many (<see cref="GuidList"/>).</summary>
    public static ICriterion ChildrenOf(IEnumerable<Guid> parentIds) => GuidList.In("ParentId", parentIds);

    public static async Task<IReadOnlyList<Guid>> DescendantsAsync(ISession session, Page page, bool trash, CancellationToken ct)
    {
        var spaceId = page.SpaceId;
        var batchId = page.TrashBatchId;
        var result = new HashSet<Guid> { page.Id };
        var frontier = new List<Guid> { page.Id };
        while (frontier.Count > 0)
        {
            var level = session.QueryOver<Page>()
                .Where(x => x.SpaceId == spaceId && x.IsTrashed == trash)
                .And(ChildrenOf(frontier));
            if (trash)
            {
                // A purged page of the batch stays a tombstone: a restore of its ancestor leaves it.
                level = level.And(x => x.TrashBatchId == batchId && x.PurgedAt == null);
            }
            var children = await level.Select(x => x.Id).ListAsync<Guid>(ct);
            frontier = children.Where(id => result.Add(id)).ToList();
        }
        return result.Order().ToArray();
    }

    public static async Task<IReadOnlyList<Guid>> ReachableAsync(ISession session, Guid rootId, int limit, CancellationToken ct)
    {
        var result = new HashSet<Guid> { rootId };
        var frontier = new List<Guid> { rootId };
        while (frontier.Count > 0 && result.Count < limit)
        {
            var children = await session.QueryOver<Page>()
                .Where(ChildrenOf(frontier))
                .Select(x => x.Id)
                .ListAsync<Guid>(ct);
            frontier = children.Where(id => result.Add(id)).ToList();
        }
        return result.Take(limit).ToArray();
    }

    public static async Task<bool> IsAncestorAsync(ISession session, Guid candidate, Guid pageId, CancellationToken ct)
    {
        var visited = new HashSet<Guid>();
        Guid? current = candidate;
        while (current is { } id && visited.Add(id))
        {
            if (id == pageId)
            {
                return true;
            }
            current = await session.QueryOver<Page>().Where(x => x.Id == id).Select(x => x.ParentId).SingleOrDefaultAsync<Guid?>(ct);
        }
        return false;
    }

    /// <summary>Locks every selected page and requires the operation on each one: every page is authorized on its own (D2),
    /// with one grant query for the whole selection. One statement locks the rows in id order and one reads them under the locks;
    /// an instance the session already holds goes through GetForUpdateAsync, which re-reads it if it was read before the lock.</summary>
    public static async Task<IReadOnlyList<Page>> LockSelectionAsync(ISession session, IContentAccess access, IAtlasLocks locks, IReadOnlyList<Guid> ids,
        AccessOperation operation, CancellationToken ct)
    {
        if ((await locks.LockPagesForUpdateAsync(ids, ct)).Count != ids.Distinct().Count())
        {
            throw OrganizationLocks.Missing();
        }
        var unheld = ids.Where(id => session.Held<Page>(id) is null).ToArray();
        var read = unheld.Length == 0 ? [] : await session.QueryOver<Page>()
            .Where(GuidList.In("Id", unheld))
            .ListAsync(ct);
        var byId = read.ToDictionary(page => page.Id);
        var pages = new List<Page>(ids.Count);
        foreach (var id in ids)
        {
            pages.Add(byId.GetValueOrDefault(id) ?? await session.GetForUpdateAsync<Page>(id, ct) ?? throw OrganizationLocks.Missing());
        }
        foreach (var authorization in await access.AuthorizePagesAsync(pages, operation, ct))
        {
            authorization.Require();
        }
        return pages;
    }
}
