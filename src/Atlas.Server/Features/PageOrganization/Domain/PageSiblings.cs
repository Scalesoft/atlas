using NHibernate.Criterion;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Domain;

public static class PageSiblings
{
    public static Task<int> PositionAsync(ISession session, Page page, Guid? parentId, Guid? beforeId, UserAccount actor, CancellationToken ct)
    {
        return PositionAsync(session, [], page.SpaceId, page.Id, parentId, beforeId, actor, ct);
    }

    /// <summary>
    /// The sort order of a node (a page, or another type's: <paramref name="others"/>) placed under <paramref name="parentId"/>
    /// before <paramref name="beforeId"/> (null: at the end). Pages and the other types' nodes share one sibling order, so the
    /// neighbours and, when a level's gaps run out, the renumbering cover them all.
    /// </summary>
    public static async Task<int> PositionAsync(ISession session, IReadOnlyList<IContentTreeParticipant> others, Guid spaceId, Guid nodeId,
        Guid? parentId, Guid? beforeId, UserAccount actor, CancellationToken ct)
    {
        var query = session.QueryOver<Page>().Where(x => x.SpaceId == spaceId && !x.IsTrashed && !x.IsTemplate && x.Id != nodeId);
        query = parentId is { } parent ? query.Where(x => x.ParentId == parent) : query.Where(x => x.ParentId == null);
        var siblings = (await query.ListAsync(ct)).Select(page => new Sibling(page.Id, page.SortOrder, page, null)).ToList();
        foreach (var other in others)
        {
            siblings.AddRange((await other.SiblingsAsync(spaceId, parentId, ct)).Where(node => node.Id != nodeId)
                .Select(node => new Sibling(node.Id, node.SortOrder, null, other)));
        }
        siblings.Sort((a, b) => Compare(a.SortOrder, a.Id, b.SortOrder, b.Id));
        var index = siblings.Count;
        if (beforeId is { } before)
        {
            index = siblings.FindIndex(x => x.Id == before);
            // Only a sibling the account can list (WP3 S3) may anchor the position; an unlisted one answers as a missing one.
            // Another type's node is listed to whoever may place one here (a space role).
            if (index < 0 || siblings[index].Page is not null && !await IsListedAsync(session, before, actor, ct))
            {
                throw new ApiException(400, "Cílová pozice není dostupná.");
            }
        }
        var lower = index == 0 ? 0L : siblings[index - 1].SortOrder;
        var upper = index == siblings.Count ? lower + 2048 : siblings[index].SortOrder;
        if (upper - lower > 1 && lower + (upper - lower) / 2 <= int.MaxValue)
        {
            return (int)(lower + (upper - lower) / 2);
        }
        // Rare gap exhaustion: normalize only these siblings, never all documents or revisions.
        if (siblings.Count >= int.MaxValue / 1024 - 1)
        {
            throw new ApiException(409, "Pro tuto úroveň již není dostupná pozice.");
        }
        var reordered = new Dictionary<IContentTreeParticipant, Dictionary<Guid, int>>();
        for (var i = 0; i < siblings.Count; i++)
        {
            if (siblings[i].Page is { } page)
            {
                page.SortOrder = (i + 1) * 1024;
            }
            else
            {
                (reordered.TryGetValue(siblings[i].Owner!, out var orders) ? orders : reordered[siblings[i].Owner!] = [])[siblings[i].Id] = (i + 1) * 1024;
            }
        }
        foreach (var (owner, orders) in reordered)
        {
            await owner.ReorderAsync(orders, ct);
        }
        await session.FlushAsync(ct);
        return index == 0 ? 512 : index == siblings.Count ? (index + 1) * 1024 : index * 1024 + 512;
    }

    /// <summary>The tree's sibling order: SortOrder, then the id as PostgreSQL orders uuids (byte by byte, as their text does).</summary>
    public static int Compare(int sortA, Guid idA, int sortB, Guid idB)
    {
        return sortA != sortB ? sortA.CompareTo(sortB) : string.CompareOrdinal(idA.ToString("N"), idB.ToString("N"));
    }

    private static async Task<bool> IsListedAsync(ISession session, Guid pageId, UserAccount actor, CancellationToken ct)
    {
        var actorId = actor.Id;
        return await session.QueryOver<PageAccessEntry>()
            .Where(x => x.UserId == actorId && x.PageId == pageId)
            .And(PageListing.Listed(Projections.Property<PageAccessEntry>(x => x.IsPublished), Projections.Property<PageAccessEntry>(x => x.OwnerId),
                Projections.Property<PageAccessEntry>(x => x.Role), actor))
            .RowCountAsync(ct) > 0;
    }

    private sealed record Sibling(Guid Id, int SortOrder, Page? Page, IContentTreeParticipant? Owner);
}
