using System.Globalization;
using Atlas.Server.Features.Boards;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Domain;
using Atlas.Server.Platform.Resources;

namespace Atlas.Server.Features.Relations.Queries;

/// <param name="Drafts">Also the uses only a page's working revision holds, to readers who may edit that page.</param>
/// <param name="Cursor">nextCursor of the previous page.</param>
/// <param name="Part">Only the uses of one part of the object, the edges' anchor: a catalog requirement's key on its catalog page.</param>
public sealed record GetObjectUsage(Guid ObjectId, bool Drafts = true, string? Cursor = null, string? Part = null)
{
    public const int PageSize = 50;
}

/// <param name="Fresh">For the last-usage warning of a delete or removal: the mentions a save left behind are derived first.</param>
public sealed record CountObjectUsage(Guid ObjectId, bool Drafts = true, string? Part = null, bool Fresh = false);

/// <param name="Type">Only components of this type id.</param>
/// <param name="Unused">Only the unused ones (UsageInventoryRow.Unused).</param>
public sealed record ListSpaceUsage(Guid SpaceId, string? Type = null, bool Unused = false, string? Cursor = null)
{
    public const int PageSize = 50;
}

/// <summary>„Použito na“ of one object (ObjectUsage), a page of its uses with why each is there; 404 for an object the reader may not
/// read. Constant statements: one per relation source, one for the nodes, one per kind of place for the reasons of the page.</summary>
public sealed class GetObjectUsageHandler(IContentAccess access, ObjectUsage usage, UsageReasons reasons)
{
    public async Task<ObjectUsageList> Handle(GetObjectUsage query, CancellationToken ct)
    {
        var from = UsagePaging.Offset(query.Cursor);
        var actor = await access.LockActorAsync(null, ct);
        var uses = await usage.ReadAsync(actor, query.ObjectId, query.Drafts, ct, query.Part);
        var page = uses.Visible
            .OrderBy(use => use.Place.SpaceName ?? "", UsagePaging.Titles).ThenBy(use => use.Place.SpaceId)
            .ThenBy(use => use.Place.Title, UsagePaging.Titles).ThenBy(use => use.Place.Id).ThenBy(use => use.Edge.Kind, StringComparer.Ordinal)
            .Skip(from).Take(GetObjectUsage.PageSize).ToArray();
        var why = await reasons.ReadAsync(query.ObjectId, page.Select(use => use.Edge).ToArray(), ct);
        var rows = page.Select(use =>
        {
            var reason = why.GetValueOrDefault(use.Edge);
            return new UsageRow(use.Place.Id, use.Place.TypeId, use.Place.Title, use.Place.SpaceId, use.Place.SpaceName, use.Edge.Kind, use.Edge.Count,
                use.Edge.Anchor, use.Edge.Draft, reason?.PartId, reason?.Excerpt, reason?.Card ?? false);
        }).ToArray();
        var next = from + page.Length;
        return new ObjectUsageList(query.ObjectId, rows, uses.Total, uses.Hidden, next < uses.Visible.Count ? UsagePaging.Cursor(next) : null);
    }
}

/// <summary>The count of badges and of the last-usage warning: the same uses as <see cref="GetObjectUsageHandler"/>, without reasons.</summary>
public sealed class CountObjectUsageHandler(IContentAccess access, ObjectUsage usage, PageObjectMentions mentions)
{
    public async Task<UsageCount> Handle(CountObjectUsage query, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        // The mentions are derived after a save commits: the last-usage warning refreshes the pages that may still be behind first, so it
        // never misses a mention saved a moment ago. The badges' count does not (it is asked per candidate and writes nothing).
        if (query.Fresh)
        {
            await mentions.RefreshStaleAsync(query.ObjectId, ct);
        }
        var uses = await usage.ReadAsync(actor, query.ObjectId, query.Drafts, ct, query.Part);
        return new UsageCount(query.ObjectId, uses.Total, uses.Visible.Count, uses.Hidden);
    }
}

/// <summary>
/// The space's inventory (plan 3.1 M5): the pages, boards and objects of the space the reader may read, and the wiki's objects used in
/// it, by type, each with its uses (drafts included, ObjectUsage) and whether it is unused. Constant statements, as the space's contents
/// (ListSpaceContentsHandler), whose page list rule (404 alike) it shares; a page's own board is no row of its own.
/// </summary>
// shortcut: the whole space is read per request as Obsah is (ContentsBudgetTests, UsageBudgetTests); window it in SQL when spaces outgrow it.
public sealed class ListSpaceUsageHandler(IContentAccess access, PageListing listing, RelationReader reader)
{
    public async Task<SpaceUsage> Handle(ListSpaceUsage query, CancellationToken ct)
    {
        if (query.Type is { Length: > 64 })
        {
            throw new ApiException(400, "Filtr je příliš dlouhý.", "invalid_filter");
        }
        var from = UsagePaging.Offset(query.Cursor);
        var actor = await access.LockActorAsync(query.SpaceId, ct);
        await listing.RequireSpaceAsync(actor, query.SpaceId, ct);
        var edges = await reader.EdgesAsync(query.SpaceId, null, ct, drafts: true);
        var graph = await reader.GraphAsync(actor, query.SpaceId, edges, [], ct);
        var uses = edges.Where(edge => RelationKinds.UsedEnd(edge) != RelationKinds.PlaceOf(edge)).GroupBy(RelationKinds.UsedEnd)
            .ToDictionary(group => group.Key, group => ObjectUsage.Split(group, graph));
        var here = graph.Nodes.Values.Where(node => node.Visible && node.SpaceId == query.SpaceId).Select(node => node.Id).Append(query.SpaceId).ToHashSet();
        // The wiki's (or the reader's private) objects used in a place of this space the reader sees.
        var usedHere = uses.Where(entry => entry.Value.Visible.Any(use => here.Contains(use.Place.Id))).Select(entry => entry.Key).ToHashSet();
        var rows = graph.Nodes.Values
            .Where(node => node.Visible && node.HostId is null && (node.SpaceId == query.SpaceId || node.Scope != ObjectScopes.Space && usedHere.Contains(node.Id)))
            .Select(node =>
            {
                var used = uses.GetValueOrDefault(node.Id);
                var inTree = node.TypeId is PagesAtlasModule.ContentType or BoardsAtlasModule.ContentType;
                return new UsageInventoryRow(node.Id, node.TypeId, node.Title, node.Scope, used?.Visible.Count ?? 0, used?.Hidden ?? 0, !inTree && (used?.Total ?? 0) == 0);
            })
            .ToArray();
        var shown = rows.Where(row => (query.Type is null || row.TypeId == query.Type) && (!query.Unused || row.Unused))
            .OrderBy(row => row.TypeId, StringComparer.Ordinal).ThenBy(row => row.Title, UsagePaging.Titles).ThenBy(row => row.Id)
            .Skip(from).Take(ListSpaceUsage.PageSize + 1).ToArray();
        var page = shown.Take(ListSpaceUsage.PageSize).ToArray();
        return new SpaceUsage(page, rows.GroupBy(row => row.TypeId).ToDictionary(group => group.Key, group => group.Count()), rows.Count(row => row.Unused),
            shown.Length > ListSpaceUsage.PageSize ? UsagePaging.Cursor(from + page.Length) : null);
    }
}

/// <summary>Paging by position: both lists are read whole and sorted in memory, so the position is stable while nothing changes.</summary>
internal static class UsagePaging
{
    public static readonly StringComparer Titles = StringComparer.Create(CultureInfo.GetCultureInfo("cs-CZ"), CompareOptions.IgnoreCase);

    public static string Cursor(int offset) => offset.ToString(CultureInfo.InvariantCulture);

    public static int Offset(string? cursor)
    {
        if (cursor is null)
        {
            return 0;
        }
        return int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset <= 1_000_000
            ? offset
            : throw new ApiException(400, "Neplatný kurzor; začněte znovu bez něj.", "invalid_cursor");
    }
}
