using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Platform.Validation;

namespace Atlas.Server.Features.Boards.Domain;

/// <summary>
/// The rules of a board batch (boards spec §5), without NHibernate: the changes apply in order over the committed state of the
/// items they touch, all of them or none. The server's order decides conflicts, so a later batch wins per property: a change
/// sets only the properties it names. Rejections carry a code and never the offending input.
/// </summary>
public static class BoardEditing
{
    public const int MaxChanges = 200;
    /// <summary>Counts the stored live rows, connectors hidden by a deleted end included: it bounds storage and what a board read
    /// loads, so the count a reader sees in the list (without hidden connectors) may be lower.</summary>
    public const int MaxLiveItems = 5000;
    public const int MaxText = 4000;
    /// <summary>The live items' text and props of one board in UTF-8 bytes (L3): 4 MiB, the largest collaborative document
    /// (MaxDocumentBytes), so a board never costs more to open than a page; per item the text and block caps hold besides.</summary>
    public const long MaxBoardBytes = 4L * 1024 * 1024;
    public const int MaxTitle = 200;
    public const double MaxCoordinate = 1_000_000;
    public const double MinSize = 8;
    public const double MaxSize = 20_000;
    /// <summary>A connector's label (its text): one line.</summary>
    public const int MaxLabel = 200;
    /// <summary>How many groups and frames an item may be nested in (objects spec §5.4).</summary>
    public const int MaxDepth = 4;
    /// <summary>The board's own colours (notes, shapes, frames, cards) in the order its palette shows them; the web maps them to Atlas
    /// tokens. An editor component's background may also be any appearance-palette id (Colors).</summary>
    public static readonly IReadOnlyList<string> Palette = ["yellow", "orange", "red", "pink", "purple", "blue", "teal", "green", "gray", "white", "black"];
    /// <summary>The board's colours and the appearance palette of spaces (<c>blue-4</c>): an editor component's background takes any.</summary>
    public static readonly IReadOnlySet<string> Colors =
        Palette.Concat(AppearanceCatalog.Palette.Select(color => color.Id)).ToHashSet(StringComparer.Ordinal);

    public static BoardEditResult Apply(BoardState state, IReadOnlyList<BoardChange>? changes, long sequence)
    {
        if (changes is null || changes.Count is 0 or > MaxChanges)
        {
            return Rejected(state, BoardRejection.Invalid);
        }
        var items = state.Items.ToDictionary(item => item.Id);
        var changed = new Dictionary<Guid, BoardItemState>();
        var title = state.Title;
        var settings = state.Settings;
        var live = state.LiveItemCount;
        var parents = new Dictionary<Guid, Guid>(state.Parents ?? new Dictionary<Guid, Guid>());
        foreach (var change in changes)
        {
            if (change is null)
            {
                return Rejected(state, BoardRejection.Invalid);
            }
            string? rejection;
            BoardItemState? next = null;
            if (change.Type == "rename")
            {
                rejection = Title(change.Title, out title);
            }
            else if (change.Type == "settings")
            {
                var merged = change.Settings is { Count: > 0 } patch ? BoardProps.MergeSettings(settings, patch) : null;
                rejection = merged is null ? BoardRejection.Invalid : null;
                settings = merged ?? settings;
            }
            else if (change.Type == "create")
            {
                rejection = Create(state, items, change.Item, sequence, out next);
            }
            else if (change.ItemId is not { } id)
            {
                rejection = BoardRejection.Invalid;
            }
            else if (!items.TryGetValue(id, out var item))
            {
                rejection = change.Type is "move" or "set" or "reorder" or "delete" or "restore" or "parent" ? BoardRejection.ItemMissing : BoardRejection.Invalid;
            }
            else if (change.Type == "restore")
            {
                rejection = null;
                next = item.IsDeleted ? item with { IsDeleted = false, Version = sequence } : null;
            }
            else if (item.IsDeleted)
            {
                rejection = change.Type is "move" or "set" or "reorder" or "delete" or "parent" ? BoardRejection.ItemDeleted : BoardRejection.Invalid;
            }
            else if (change.Type == "parent")
            {
                rejection = Join(items, parents, item, change.ParentId, sequence, out next);
            }
            else
            {
                rejection = Edit(items, item, change, sequence, out next);
            }
            if (rejection is not null)
            {
                return Rejected(state, rejection);
            }
            if (next is not null)
            {
                live += Live(next) - (items.TryGetValue(next.Id, out var before) ? Live(before) : 0);
                items[next.Id] = next;
                changed[next.Id] = next;
            }
        }
        return live > MaxLiveItems ? Rejected(state, BoardRejection.Invalid) : new BoardEditResult(null, title, changed.Values.ToArray(), live, settings);
    }

    /// <summary>
    /// The changes as the server applies them, and so as the log stores and the channel broadcasts them: a rename's title trimmed,
    /// and of every change only the members its type uses (a move that names a text changes no text, so nobody is told one), props
    /// canonical (a create without them carries <c>{}</c>, a set keeps exactly the keys it names, nulls included). Free of state, so
    /// a retry of the raw request normalizes to the same changes; idempotency compares these.
    /// </summary>
    public static IReadOnlyList<BoardChange> Normalize(IReadOnlyList<BoardChange> changes)
    {
        return changes.Select(change => change switch
        {
            { Type: "create" } => new BoardChange(change.Type, Item: change.Item is { } item ? item with { Props = BoardProps.Canonical(item.Props ?? []) } : null),
            { Type: "move" } => new BoardChange(change.Type, ItemId: change.ItemId, X: change.X, Y: change.Y, Width: change.Width, Height: change.Height),
            { Type: "set" } => new BoardChange(change.Type, ItemId: change.ItemId, Text: change.Text, Color: change.Color,
                Props: change.Props is { } props ? BoardProps.Canonical(props) : null, From: change.From, To: change.To),
            { Type: "reorder" } => new BoardChange(change.Type, ItemId: change.ItemId, Z: change.Z),
            { Type: "delete" or "restore" } => new BoardChange(change.Type, ItemId: change.ItemId),
            { Type: "parent" } => new BoardChange(change.Type, ItemId: change.ItemId, ParentId: change.ParentId),
            { Type: "rename" } => new BoardChange(change.Type, Title: change.Title?.Trim()),
            { Type: "settings" } => new BoardChange(change.Type, Settings: change.Settings is { } settings ? BoardProps.Canonical(settings) : null),
            // Null or of an unknown type: Apply refuses it as it is.
            _ => change
        }).ToArray();
    }

    /// <summary>The items a reader sees: live ones, without a connector whose end is deleted or gone (restoring the end shows it
    /// again).</summary>
    public static IReadOnlyList<BoardItemState> Visible(IReadOnlyCollection<BoardItemState> items)
    {
        var live = items.Where(item => !item.IsDeleted).Select(item => item.Id).ToHashSet();
        return items.Where(item => !item.IsDeleted
            && (item.Kind != BoardItemKinds.Connector || live.Contains(item.FromItemId ?? Guid.Empty) && live.Contains(item.ToItemId ?? Guid.Empty)))
            .ToArray();
    }

    /// <summary>
    /// A batch as who may not write the board hears it, live or caught up (review S1): the log by the rule of GET (<see cref="Visible"/>).
    /// Of an item it does not see now (<paramref name="hidden"/>, GetBoardOperationsHandler.HiddenAsync) neither the create nor a set
    /// reaches it, so no text, props or target of a removed item does, and nothing at all of an item whose create it was not told
    /// (<paramref name="untold"/>, shared by the batches of one answer). A delete, restore or move of an item it may hold from before
    /// stays; one it does not hold makes its client reload, as before. Writers see removed items (GetRemovedBoardItems) and hear it all.
    /// </summary>
    public static IReadOnlyList<BoardChange> ForReader(IReadOnlyList<BoardChange> changes, IReadOnlySet<Guid> hidden, ISet<Guid> untold)
    {
        var told = new List<BoardChange>(changes.Count);
        foreach (var change in changes)
        {
            if ((change.Item?.Id ?? change.ItemId) is { } id && hidden.Contains(id))
            {
                if (change.Type == "create")
                {
                    untold.Add(id);
                }
                if (change.Type is "create" or "set" || untold.Contains(id))
                {
                    continue;
                }
            }
            told.Add(change);
        }
        return told;
    }

    /// <summary>
    /// The batches a reader catches up with (L1): every text, title, props key and settings key carries its last value in
    /// <paramref name="batches"/>, which end at the board's sequence, so applied in order they give the board as it is now and no
    /// value a later batch replaced (a password pasted and removed) reaches who may not write. Writers read the log as written.
    /// </summary>
    public static IReadOnlyList<BoardBatchDto> LatestValues(IReadOnlyList<BoardBatchDto> batches)
    {
        var texts = new Dictionary<Guid, string>();
        var props = new Dictionary<(Guid, string), JsonNode?>();
        var settings = new Dictionary<string, JsonNode?>();
        string? title = null;
        foreach (var change in batches.SelectMany(batch => batch.Changes))
        {
            var id = change.Item?.Id ?? change.ItemId ?? Guid.Empty;
            if ((change.Item?.Text ?? change.Text) is { } text)
            {
                texts[id] = text;
            }
            foreach (var (key, value) in change.Item?.Props ?? change.Props ?? [])
            {
                props[(id, key)] = value;
            }
            foreach (var (key, value) in change.Settings ?? [])
            {
                settings[key] = value;
            }
            title = change.Title ?? title;
        }
        JsonObject? Latest(JsonObject? part, Func<string, JsonNode?> last, bool full) => part is null ? null
            // A create's full props drop a key a later set removed; a set's part keeps the removal (null).
            : new JsonObject(part.Select(prop => KeyValuePair.Create(prop.Key, last(prop.Key)?.DeepClone())).Where(prop => !full || prop.Value is not null));
        return batches.Select(batch => batch with
        {
            Changes = batch.Changes.Select(change =>
            {
                var id = change.Item?.Id ?? change.ItemId ?? Guid.Empty;
                return change with
                {
                    Item = change.Item is { } item
                        ? item with { Text = item.Text is null ? null : texts[id], Props = Latest(item.Props, key => props[(id, key)], true) }
                        : null,
                    Text = change.Text is null ? null : texts[id],
                    Props = Latest(change.Props, key => props[(id, key)], false),
                    Settings = Latest(change.Settings, key => settings[key], false),
                    Title = change.Title is null ? null : title
                };
            }).ToArray()
        }).ToArray();
    }

    /// <summary>Every item id a batch names: the items it changes, the ids it creates, the ends of new or reconnected connectors and
    /// the new parents.</summary>
    public static IReadOnlyCollection<Guid> TouchedIds(IReadOnlyList<BoardChange>? changes)
    {
        return (changes ?? []).Where(change => change is not null)
            .SelectMany(change => new[] { change.ItemId, change.Item?.Id, change.Item?.From, change.Item?.To, change.ParentId, change.From, change.To })
            .OfType<Guid>().Distinct().ToArray();
    }

    /// <summary>Whether the batch moves items into or out of groups and frames: only then does nesting need the board's links.</summary>
    public static bool ChangesParents(IReadOnlyList<BoardChange>? changes)
    {
        return (changes ?? []).Any(change => change?.Type == "parent");
    }

    /// <summary>The objects a batch puts on the board as object cards.</summary>
    public static IReadOnlyCollection<Guid> ObjectTargets(IReadOnlyList<BoardChange>? changes)
    {
        return (changes ?? []).Where(change => change?.Type == "create" && change.Item?.Kind == BoardItemKinds.Object)
            .Select(change => BoardProps.ObjectTarget(change.Item!.Props)?.Id).OfType<Guid>().Distinct().ToArray();
    }

    /// <summary>The pages a batch puts on the board as page cards.</summary>
    public static IReadOnlyCollection<Guid> PageTargets(IReadOnlyList<BoardChange>? changes)
    {
        return (changes ?? []).Where(change => change?.Type == "create" && change.Item?.Kind == BoardItemKinds.Page)
            .Select(change => change.Item!.TargetResourceId).OfType<Guid>().Distinct().ToArray();
    }

    private static string? Create(BoardState state, Dictionary<Guid, BoardItemState> items, BoardItemInput? input, long sequence, out BoardItemState? item)
    {
        item = null;
        if (input is not { Id: { } id, Kind: { } kind } || id == Guid.Empty || !BoardProps.IsKind(kind) || !Geometry(input.X, input.Y, input.Width, input.Height)
            || !Layer(input.Z) || !Text(kind, input.Text) || input.Color is not null && !Colors.Contains(input.Color)
            || BoardProps.Create(kind, input.Props ?? []) is not { } props)
        {
            return BoardRejection.Invalid;
        }
        if (items.ContainsKey(id) || state.ForeignIds.Contains(id))
        {
            return BoardRejection.ItemExists;
        }
        var ends = input.From is not null || input.To is not null;
        var target = input.TargetResourceId;
        switch (kind)
        {
            case BoardItemKinds.Page when input.TargetResourceId is { } page && state.ReadablePages.Contains(page) && !ends:
                break;
            case BoardItemKinds.Connector when input.TargetResourceId is null && input is { From: { } from, To: { } to } && from != to:
                if ((End(items, from) ?? End(items, to)) is { } refused)
                {
                    return refused;
                }
                break;
            // An object the author may read, of the type the props say; its target is that object.
            case BoardItemKinds.Object when BoardProps.ObjectTarget(input.Props) is { } shown && (target ?? shown.Id) == shown.Id && !ends
                                           && state.ReadableObjects?.GetValueOrDefault(shown.Id) == shown.TypeId:
                target = shown.Id;
                break;
            case not (BoardItemKinds.Page or BoardItemKinds.Connector or BoardItemKinds.Object) when input.TargetResourceId is null && !ends:
                break;
            default:
                return BoardRejection.Invalid;
        }
        item = new BoardItemState(id, kind, input.X!.Value, input.Y!.Value, input.Width!.Value, input.Height!.Value, (int)input.Z!.Value,
            input.Text, input.Color, target, input.From, input.To, sequence, false, props);
        return null;
    }

    /// <summary>A connector end: a live item of this board that is not a connector itself.</summary>
    private static string? End(Dictionary<Guid, BoardItemState> items, Guid id)
    {
        if (!items.TryGetValue(id, out var end))
        {
            return BoardRejection.ItemMissing;
        }
        if (end.IsDeleted)
        {
            return BoardRejection.ItemDeleted;
        }
        return end.Kind == BoardItemKinds.Connector ? BoardRejection.Invalid : null;
    }

    /// <summary>
    /// An item into a live group or frame of the board, or out of its own with no parent. Never a connector (it follows its ends),
    /// never into itself or what it holds, and with everything it holds at most <see cref="MaxDepth"/> levels down. The links are
    /// the board's with the batch's earlier changes, and take this one.
    /// </summary>
    private static string? Join(Dictionary<Guid, BoardItemState> items, Dictionary<Guid, Guid> parents, BoardItemState item, Guid? parentId, long sequence,
        out BoardItemState? next)
    {
        next = null;
        if (item.Kind == BoardItemKinds.Connector)
        {
            return BoardRejection.Invalid;
        }
        if (parentId is { } id)
        {
            if (!items.TryGetValue(id, out var parent))
            {
                return BoardRejection.ItemMissing;
            }
            if (parent.IsDeleted)
            {
                return BoardRejection.ItemDeleted;
            }
            if (parent.Kind is not (BoardItemKinds.Group or BoardItemKinds.Frame) || Ancestors(parents, id, item.Id) is not { } above
                || above + 1 + Height(parents, item.Id) > MaxDepth)
            {
                return BoardRejection.Invalid;
            }
            parents[item.Id] = id;
        }
        else
        {
            parents.Remove(item.Id);
        }
        next = item with { ParentId = parentId, Version = sequence };
        return null;
    }

    /// <summary>How many containers hold <paramref name="id"/>; null when <paramref name="joining"/> is the id itself or one of
    /// them (a cycle). Counting stops past the limit, so it ends over any links.</summary>
    private static int? Ancestors(Dictionary<Guid, Guid> parents, Guid id, Guid joining)
    {
        var count = 0;
        for (var current = id; count <= MaxDepth; count++)
        {
            if (current == joining)
            {
                return null;
            }
            if (!parents.TryGetValue(current, out current))
            {
                return count;
            }
        }
        return count;
    }

    /// <summary>How many levels of members lie under <paramref name="id"/> (0 when it holds nothing), counted to the limit.</summary>
    private static int Height(Dictionary<Guid, Guid> parents, Guid id)
    {
        var level = new HashSet<Guid> { id };
        var height = 0;
        while (height <= MaxDepth)
        {
            level = parents.Where(link => level.Contains(link.Value)).Select(link => link.Key).ToHashSet();
            if (level.Count == 0)
            {
                break;
            }
            height++;
        }
        return height;
    }

    private static string? Edit(Dictionary<Guid, BoardItemState> items, BoardItemState item, BoardChange change, long sequence, out BoardItemState? next)
    {
        next = null;
        switch (change.Type)
        {
            case "move" when Geometry(change.X, change.Y, change.Width, change.Height):
                next = item with { X = change.X!.Value, Y = change.Y!.Value, Width = change.Width!.Value, Height = change.Height!.Value };
                break;
            case "set" when (change.Text is not null || change.Color is not null || change.Props is { Count: > 0 } || change.From is not null || change.To is not null)
                            && Text(item.Kind, change.Text) && (change.Color is null or "" || Colors.Contains(change.Color)):
                var props = change.Props is { Count: > 0 } patch ? BoardProps.Merge(item.Kind, item.Props, patch) : item.Props;
                if (props is null)
                {
                    return BoardRejection.Invalid;
                }
                // New ends keep the connector (its id, label and style): a new end is a live item of the board, never the other end. The
                // end that stays was checked when it was set (and is not loaded: a batch loads only the items it names).
                var from = change.From ?? item.FromItemId;
                var to = change.To ?? item.ToItemId;
                if (change.From is not null || change.To is not null)
                {
                    if (item.Kind != BoardItemKinds.Connector || from is null || from == to)
                    {
                        return BoardRejection.Invalid;
                    }
                    if (((change.From is { } start ? End(items, start) : null) ?? (change.To is { } end ? End(items, end) : null)) is { } refused)
                    {
                        return refused;
                    }
                }
                // An empty colour removes it, as an empty text empties the text.
                next = item with { Text = change.Text ?? item.Text, Color = change.Color is "" ? null : change.Color ?? item.Color, Props = props, FromItemId = from, ToItemId = to };
                break;
            case "reorder" when Layer(change.Z):
                next = item with { Z = (int)change.Z!.Value };
                break;
            case "delete":
                next = item with { IsDeleted = true };
                break;
            default:
                return BoardRejection.Invalid;
        }
        next = next with { Version = sequence };
        return null;
    }

    private static string? Title(string? value, out string title)
    {
        title = value?.Trim() ?? "";
        return title.Length is 0 or > MaxTitle || !Rules.IsLine(title) ? BoardRejection.Invalid : null;
    }

    private static bool Geometry(double? x, double? y, double? width, double? height)
    {
        return x is { } left && y is { } top && width is { } w && height is { } h
            && Within(left, -MaxCoordinate, MaxCoordinate) && Within(top, -MaxCoordinate, MaxCoordinate)
            && Within(w, MinSize, MaxSize) && Within(h, MinSize, MaxSize);
    }

    // NaN fails both comparisons and the infinities the bounds, so the range check is also the finiteness check.
    private static bool Within(double value, double min, double max)
    {
        return value >= min && value <= max;
    }

    private static bool Layer(long? z)
    {
        return z is >= int.MinValue and <= int.MaxValue;
    }

    /// <summary>An item's text, kept as sent ("" empties it): several lines as a description (Rules.IsMultiLineText), a
    /// connector's label and a group's name one line of at most <see cref="MaxLabel"/> characters.</summary>
    private static bool Text(string kind, string? text)
    {
        return text is null || (kind is BoardItemKinds.Connector or BoardItemKinds.Group
            ? text.Length <= MaxLabel && Rules.IsLine(text)
            : text.Length <= MaxText && Rules.IsMultiLineText(text));
    }

    private static int Live(BoardItemState item)
    {
        return item.IsDeleted ? 0 : 1;
    }

    private static BoardEditResult Rejected(BoardState state, string code)
    {
        return new BoardEditResult(code, state.Title, [], state.LiveItemCount, state.Settings);
    }
}
