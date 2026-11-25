using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using Atlas.Server.Features.Boards;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Domain;
using Atlas.Server.Features.Relations.Persistence;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Platform.Resources;

namespace Atlas.Server.Features.Relations.Queries;

/// <summary>
/// The space's contents tab (spec 2026-10-27 §4, §13.3): its pages, boards and objects the reader may read, the wiki's (and the
/// reader's private) objects used in it, each with its uses, links and comments counted over what the reader may read only. A
/// page's own board is no row of its own: relation <c>attached</c> lists them with their page. Constant statements: the actor and the
/// space as the space's page list (404 alike), one per relation source, one for every node and its comments, and with wiki objects
/// shown the wiki-write decision of their types. The rows are
/// filtered, sorted and cut in memory from that one read, so the summary and every page agree.
/// </summary>
// shortcut: the whole space is read per request (budget p95 150 ms for 5 000 objects, ContentsBudgetTests; measured ~170-210 ms warm at 10 000
// pages, ~90 ms of it the nodes statement, once PostgreSQL's JIT is off, RelationNodes.ReadAsync); window it in SQL when spaces outgrow that.
public sealed class ListSpaceContentsHandler(IContentAccess access, PageListing listing, RelationReader reader)
{
    private static readonly string[] RelationFilters = ["unused", "noIncoming", "broken", "attached", "mostUsed"];
    private static readonly string[] Sorts = ["title", "updated", "used"];
    // Natural order: „Poznámka 2“ before „Poznámka 10“.
    private static readonly StringComparer Titles = StringComparer.Create(CultureInfo.GetCultureInfo("cs-CZ"), CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    public async Task<SpaceContents> Handle(ListSpaceContents query, CancellationToken ct)
    {
        if (query.Relation is { } relation && !RelationFilters.Contains(relation))
        {
            throw new ApiException(400, "Vztah je unused, noIncoming, broken, attached nebo mostUsed.", "invalid_relation");
        }
        var sort = query.Sort ?? (query.Relation == "mostUsed" ? "used" : "title");
        if (!Sorts.Contains(sort))
        {
            throw new ApiException(400, "Řazení je title, updated nebo used.", "invalid_sort");
        }
        if (query.Q is { Length: > 200 } || query.Type is { Length: > 64 })
        {
            throw new ApiException(400, "Filtr je příliš dlouhý.", "invalid_filter");
        }
        var after = ReadCursor(query.Cursor, sort);
        var actor = await access.LockActorAsync(query.SpaceId, ct);
        await listing.RequireSpaceAsync(actor, query.SpaceId, ct);
        // The links and the graph are the publications'; „Použito“ counts the places as „Použito na“ and atlas_list_usage do (ObjectUsage:
        // links and drafts, the reader's places only), and a use of any kind, hidden places too, keeps an object from being unused, as from the cleanup.
        var edges = await reader.EdgesAsync(query.SpaceId, null, ct, drafts: true);
        var graph = await reader.GraphAsync(actor, query.SpaceId, edges.Where(edge => !edge.Draft).ToArray(), [], ct);
        var uses = edges.Where(edge => RelationKinds.UsedEnd(edge) != RelationKinds.PlaceOf(edge)).GroupBy(RelationKinds.UsedEnd)
            .ToDictionary(group => group.Key, group =>
            {
                var split = ObjectUsage.Split(group, graph);
                // A reference node at the root of this space is a use the reader sees, though the space is no node (Split counts it as hidden).
                return (Shown: split.Visible.Count + group.Count(edge => RelationKinds.PlaceOf(edge) == query.SpaceId), Used: split.Total > 0);
            });
        var rows = Rows(graph, query.SpaceId, uses);
        var wikiWriters = await reader.WikiWriterTypesAsync(actor, rows.Where(row => row.Item.Scope == ObjectScopes.Wiki).Select(row => row.Item.TypeId), ct);
        rows = rows.Select(row => row.Item.Scope == ObjectScopes.Wiki && wikiWriters.Contains(row.Item.TypeId) ? row with { Item = row.Item with { CanEdit = true } } : row)
            .ToList();

        var listed = rows.Where(row => !row.Attached).ToArray();
        var summary = new ContentSummary(listed.GroupBy(row => row.Item.TypeId).ToDictionary(group => group.Key, group => group.Count()),
            new RelationCounts(listed.Count(row => row.Unused), listed.Count(row => row.NoIncoming), rows.Count(row => row.Broken), rows.Count(row => row.Attached)));

        var words = SearchText.NormalizeContent(query.Q ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = (query.Relation switch
            {
                "unused" => listed.Where(row => row.Unused),
                "noIncoming" => listed.Where(row => row.NoIncoming),
                "broken" => rows.Where(row => row.Broken),
                "attached" => rows.Where(row => row.Attached),
                _ => listed
            })
            .Where(row => query.Type is null || row.Item.TypeId == query.Type)
            .Where(row => words.Length == 0 || words.All(SearchText.NormalizeContent(row.Item.Title).Contains))
            .Select(row => (Key: Key(row, sort), row.Item))
            .Where(row => after is null || Compare(row.Key, after) > 0)
            .OrderBy(row => row.Key, Comparer<SortKey>.Create(Compare))
            .Take(ListSpaceContents.PageSize + 1)
            .ToArray();
        var page = shown.Take(ListSpaceContents.PageSize).ToArray();
        return new SpaceContents(page.Select(row => row.Item).ToArray(), summary,
            shown.Length > ListSpaceContents.PageSize ? WriteCursor(page[^1].Key, sort) : null);
    }

    private sealed record Row(ContentRow Item, bool Attached, bool Unused, bool NoIncoming, bool Broken, int Incoming);

    /// <param name="uses">The places each object is used, those the reader may not see too: unused is what the orphan cleanup decides; only the visible ones are counted.</param>
    private static List<Row> Rows(RelationGraph graph, Guid spaceId, IReadOnlyDictionary<Guid, (int Shown, bool Used)> uses)
    {
        var used = new Dictionary<Guid, int>();
        var linksIn = new Dictionary<Guid, int>();
        var linksOut = new Dictionary<Guid, int>();
        foreach (var edge in graph.Visible)
        {
            switch (edge.Kind)
            {
                case RelationKinds.Link:
                    linksOut[edge.SourceId] = linksOut.GetValueOrDefault(edge.SourceId) + 1;
                    linksIn[edge.TargetId] = linksIn.GetValueOrDefault(edge.TargetId) + 1;
                    break;
                case RelationKinds.Placement:
                    used[edge.SourceId] = used.GetValueOrDefault(edge.SourceId) + edge.Count;
                    break;
                case RelationKinds.Mention:
                case RelationKinds.Reference:
                case RelationKinds.Party:
                case RelationKinds.Catalog:
                    used[edge.TargetId] = used.GetValueOrDefault(edge.TargetId) + edge.Count;
                    break;
            }
        }
        var broken = graph.Broken.Select(entry => entry.Holder).ToHashSet();
        var here = graph.Nodes.Values.Where(node => node.Visible && node.SpaceId == spaceId).Select(node => node.Id).Append(spaceId).ToHashSet();
        // The wiki's (or the reader's private) objects a visible edge ties to this space or its tree's root.
        var usedHere = graph.Visible.Where(edge => here.Contains(edge.SourceId) || here.Contains(edge.TargetId))
            .SelectMany(edge => new[] { edge.SourceId, edge.TargetId }).ToHashSet();
        var rows = new List<Row>();
        foreach (var node in graph.Nodes.Values)
        {
            if (!node.Visible || !(node.SpaceId == spaceId || node.Scope != ObjectScopes.Space && usedHere.Contains(node.Id)))
            {
                continue;
            }
            var incoming = used.GetValueOrDefault(node.Id) + linksIn.GetValueOrDefault(node.Id);
            var attached = node.HostId is not null;
            var inTree = !attached && node.TypeId is PagesAtlasModule.ContentType or BoardsAtlasModule.ContentType;
            rows.Add(new Row(new ContentRow(node.Id, node.TypeId, node.Title, Path(graph, node), node.Scope, node.SpaceId, node.HostId, uses.GetValueOrDefault(node.Id).Shown,
                    linksIn.GetValueOrDefault(node.Id), linksOut.GetValueOrDefault(node.Id), node.Comments, node.UpdatedAt, node.CanEdit),
                attached, !inTree && !attached && !uses.GetValueOrDefault(node.Id).Used, inTree && incoming == 0, broken.Contains(node.Id), incoming));
        }
        return rows;
    }

    /// <summary>The readable ancestors up to the first one the reader may not see: the place the tree shows it (D8).</summary>
    private static string Path(RelationGraph graph, RelationNode node)
    {
        var titles = new List<string>();
        for (var parent = node.HostId ?? node.ParentId; parent is { } id && titles.Count < 64 && graph.Nodes.TryGetValue(id, out var ancestor) && ancestor.Visible;
             parent = ancestor.ParentId)
        {
            titles.Add(ancestor.Title);
        }
        titles.Reverse();
        return string.Join(" › ", titles);
    }

    /// <summary>The keyset of a sort: a number (descending orders negate it), a title, the id.</summary>
    private sealed record SortKey(long Number, string Text, Guid Id);

    private static SortKey Key(Row row, string sort) => sort switch
    {
        "updated" => new SortKey(-row.Item.UpdatedAt.Ticks, "", row.Item.Id),
        "used" => new SortKey(-row.Incoming, row.Item.Title, row.Item.Id),
        _ => new SortKey(0, row.Item.Title, row.Item.Id)
    };

    private static int Compare(SortKey? a, SortKey? b)
    {
        var number = a!.Number.CompareTo(b!.Number);
        if (number != 0)
        {
            return number;
        }
        var text = Titles.Compare(a.Text, b.Text);
        return text != 0 ? text : a.Id.CompareTo(b.Id);
    }

    /// <summary>The position after the last row of a page, for the sort it was made with.</summary>
    private sealed record Cursor(string Sort, long Number, string Text, Guid Id);

    private static string WriteCursor(SortKey key, string sort) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Cursor(sort, key.Number, key.Text, key.Id)));

    private static SortKey? ReadCursor(string? cursor, string sort)
    {
        if (cursor is null)
        {
            return null;
        }
        try
        {
            if (cursor.Length <= 2048 && JsonSerializer.Deserialize<Cursor>(Base64Url.DecodeFromChars(cursor)) is { Text: not null } position && position.Sort == sort)
            {
                return new SortKey(position.Number, position.Text, position.Id);
            }
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
        }
        throw new ApiException(400, "Neplatný kurzor; začněte znovu bez něj.", "invalid_cursor");
    }
}
