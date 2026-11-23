using System.Globalization;
using Atlas.Server.Features.Contacts.Domain;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Domain;

/// <summary>
/// „Řadit abecedně“: one level of the tree (a page's direct children, or the space root) pinned to its titles. The choice is stored
/// (<see cref="Page.SortChildrenByTitle"/>, <see cref="SpaceOrganization.SortRootByTitle"/>) and the tree read orders a pinned level
/// (<see cref="Arrange"/>), so the web, REST and MCP see one order, and a node created, renamed, restored or moved into the level needs
/// nothing: its title places it. Titles compare as plain text in Czech (ch after h, diacritics, numbers by value), ignoring case, in
/// .NET: never by database collation (the database sorts by code point). A hand move within a pinned level ends the pin
/// (<see cref="HandMoveAsync"/>); a move into it from elsewhere keeps it.
/// </summary>
public static class TitleOrder
{
    private static readonly StringComparer Titles = StringComparer.Create(CultureInfo.GetCultureInfo("cs-CZ"), CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    /// <summary>A node without a title (a reference the reader may not read) goes last.</summary>
    private static readonly Comparer<string?> Keys = Comparer<string?>.Create((a, b) => a is null ? b is null ? 0 : 1 : b is null ? -1 : Titles.Compare(a, b));

    /// <summary><paramref name="level"/> (in sibling order) by title; equal titles keep their sibling order.</summary>
    public static IEnumerable<T> ByTitle<T>(IEnumerable<T> level, Func<T, string?> title)
    {
        return level.Select(item => (Item: item, Key: title(item) is { } text ? ContactFields.InlinePlainText(text) : null))
            .OrderBy(x => x.Key, Keys).Select(x => x.Item);
    }

    /// <summary>The pinned levels of the tree read (<paramref name="items"/> in sibling order) by title, in place: every other node keeps
    /// its place. A node of a pinned level takes the level's next sort order, so the numbers agree with the list for every client.</summary>
    public static void Arrange(List<PageTreeItem> items, IReadOnlySet<Guid?> pinned)
    {
        foreach (var level in pinned)
        {
            var places = Enumerable.Range(0, items.Count).Where(index => items[index].ParentId == level).ToArray();
            var orders = places.Select(index => items[index].SortOrder).ToArray();
            var sorted = ByTitle(places.Select(index => items[index]), item => item.Readable ? item.Title : null).ToArray();
            for (var k = 0; k < places.Length; k++)
            {
                items[places[k]] = sorted[k] with { SortOrder = orders[k] };
            }
        }
    }

    public static async Task<bool> IsPinnedAsync(ISession session, Guid spaceId, Guid? parentId, CancellationToken ct)
    {
        return parentId is { } id
            ? (await session.GetAsync<Page>(id, ct))?.SortChildrenByTitle == true
            : await session.QueryOver<SpaceOrganization>().Where(x => x.SpaceId == spaceId).Select(x => x.SortRootByTitle).SingleOrDefaultAsync<bool>(ct);
    }

    /// <summary>Pins or unpins the level. Unpinned, the level keeps the order it showed: it is written as its sibling order. A change
    /// moves the tree's version, as the order every client shows changes with it. False when the level already was so.</summary>
    public static async Task<bool> SetAsync(ISession session, IReadOnlyList<IContentTreeParticipant> others, UserAccount actor, Guid spaceId, Guid? parentId,
        bool byTitle, CancellationToken ct)
    {
        if (await IsPinnedAsync(session, spaceId, parentId, ct) == byTitle)
        {
            return false;
        }
        if (!byTitle)
        {
            await WriteShownOrderAsync(session, others, actor, spaceId, parentId, ct);
        }
        if (parentId is { } id)
        {
            (await session.GetAsync<Page>(id, ct)).SortChildrenByTitle = byTitle;
            await session.FlushAsync(ct);
        }
        await session.CreateSQLQuery("""
            UPDATE "SpaceOrganization" SET "SortRootByTitle"=CASE WHEN :root THEN :byTitle ELSE "SortRootByTitle" END,"Version"="Version"+1 WHERE "SpaceId"=:space
            """).SetParameter("root", parentId is null).SetParameter("byTitle", byTitle).SetParameter("space", spaceId).ExecuteUpdateAsync(ct);
        return true;
    }

    /// <summary>
    /// A hand move (drag, keyboard, „Přesunout…“, the API and MCP) of a node from <paramref name="fromParentId"/> to
    /// <paramref name="toParentId"/>. Within a pinned level it ends the pin (<see cref="SetAsync"/>: the level keeps the order it showed),
    /// so the move then places the node among what the writer saw. Into a pinned level from elsewhere the pin stays and the node sorts in
    /// by its title. True when a pin ended.
    /// </summary>
    public static Task<bool> HandMoveAsync(ISession session, IReadOnlyList<IContentTreeParticipant> others, UserAccount actor, Guid spaceId,
        Guid? fromParentId, Guid? toParentId, CancellationToken ct)
    {
        return fromParentId != toParentId ? Task.FromResult(false) : SetAsync(session, others, actor, spaceId, toParentId, false, ct);
    }

    /// <summary>The level's nodes renumbered in title order (pages by their working title, the other types by the title their tree shows).</summary>
    // shortcut: a reference the actor may not read has no title here and goes last, as it does in the actor's tree; another reader may see it elsewhere.
    private static async Task WriteShownOrderAsync(ISession session, IReadOnlyList<IContentTreeParticipant> others, UserAccount actor, Guid spaceId,
        Guid? parentId, CancellationToken ct)
    {
        var pages = session.QueryOver<Page>().Where(x => x.SpaceId == spaceId && !x.IsTrashed && !x.IsTemplate);
        pages = parentId is { } parent ? pages.Where(x => x.ParentId == parent) : pages.Where(x => x.ParentId == null);
        var entries = session.QueryOver<PageTreeEntry>().Where(x => x.SpaceId == spaceId && !x.IsTrashed && !x.IsTemplate);
        entries = parentId is { } under ? entries.Where(x => x.ParentId == under) : entries.Where(x => x.ParentId == null);
        var titles = (await entries.Select(x => x.Id, x => x.WorkingTitle).ListAsync<object[]>(ct)).ToDictionary(row => (Guid)row[0], row => (string)row[1]);
        var level = (await pages.ListAsync(ct)).Select(page => new Node(page.Id, titles.GetValueOrDefault(page.Id), page.SortOrder, page, null)).ToList();
        foreach (var other in others)
        {
            level.AddRange((await other.NodesAsync(spaceId, actor, SpaceRole.Writer, ct))
                .Where(node => node.ParentId == parentId && node.Type != ContentTreeNode.Attached)
                .Select(node => new Node(node.Id, node.Readable ? node.Title : null, node.SortOrder, null, other)));
        }
        level.Sort((a, b) => PageSiblings.Compare(a.SortOrder, a.Id, b.SortOrder, b.Id));
        var reordered = new Dictionary<IContentTreeParticipant, Dictionary<Guid, int>>();
        var position = 0;
        foreach (var node in ByTitle(level, node => node.Title))
        {
            var order = ++position * 1024;
            if (node.Page is { } page)
            {
                page.SortOrder = order;
            }
            else
            {
                (reordered.TryGetValue(node.Owner!, out var orders) ? orders : reordered[node.Owner!] = [])[node.Id] = order;
            }
        }
        foreach (var (owner, orders) in reordered)
        {
            await owner.ReorderAsync(orders, ct);
        }
        await session.FlushAsync(ct);
    }

    private sealed record Node(Guid Id, string? Title, int SortOrder, Page? Page, IContentTreeParticipant? Owner);
}
