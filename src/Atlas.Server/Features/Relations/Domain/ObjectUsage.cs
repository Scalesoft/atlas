using System.Text.Json;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Objects.Domain;
using Atlas.Server.Features.Relations.Persistence;
using Atlas.Server.Platform.Persistence;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Relations.Domain;

/// <summary>
/// „Použito na“ (plan 3.1 §2.1, M5, U1): the one answer to where an object is used, for the web's panel, the counts of badges and of the
/// last-usage warning, the space's inventory and MCP alike. A use is an edge of the relation registry (<see cref="RelationKinds.UsedEnd"/>)
/// read from the place it is used in (<see cref="RelationKinds.PlaceOf"/>): the page that links or mentions it, the board that shows it,
/// the page a reference node or an attached board hangs under. A place in the trash is no use. A place the reader may not read, or a
/// use only the working draft of a page they may not edit holds, is counted as hidden for the server's decisions (unused, the last-usage
/// warning's yes or no) and nothing else of it is told, not even how many.
/// </summary>
public sealed class ObjectUsage(RelationReader reader)
{
    public sealed record Use(ObjectEdge Edge, RelationNode Place);

    /// <param name="Hidden">Uses in places the reader may not see.</param>
    public sealed record Uses(IReadOnlyList<Use> Visible, int Hidden)
    {
        public int Total => Visible.Count + Hidden;
    }

    /// <summary>The uses of <paramref name="objectId"/> for a reader of it (404 otherwise, as a missing object), with <paramref name="part"/>
    /// only those of one part of it (the edges' anchor: a catalog requirement's key on its catalog page). One statement per relation source
    /// and one for the nodes.</summary>
    public async Task<Uses> ReadAsync(UserAccount actor, Guid objectId, bool drafts, CancellationToken ct, string? part = null)
    {
        var edges = (await reader.EdgesAsync(Guid.Empty, [objectId], ct, drafts))
            .Where(edge => RelationKinds.UsedEnd(edge) == objectId && RelationKinds.PlaceOf(edge) != objectId)
            .Where(edge => string.IsNullOrEmpty(part) || string.Equals(edge.Anchor, part, StringComparison.Ordinal)).Distinct().ToArray();
        var graph = await reader.GraphAsync(actor, null, edges, [objectId], ct);
        if (!graph.IsVisible(objectId))
        {
            throw ObjectErrors.NotFound();
        }
        return Split(edges, graph);
    }

    /// <summary>The uses among <paramref name="edges"/> (each the use of its <see cref="RelationKinds.UsedEnd"/>) as the graph's reader sees them.</summary>
    public static Uses Split(IEnumerable<ObjectEdge> edges, RelationGraph graph)
    {
        var visible = new List<Use>();
        var hidden = 0;
        foreach (var edge in edges)
        {
            var found = graph.Nodes.TryGetValue(RelationKinds.PlaceOf(edge), out var place);
            if (found && place!.Readable && !place.Live)
            {
                continue;
            }
            if (found && place!.Visible && (!edge.Draft || place.CanEdit))
            {
                visible.Add(new Use(edge, place));
            }
            else
            {
                hidden++;
            }
        }
        return new Uses(visible, hidden);
    }
}

/// <summary>
/// Why an object is used (plan 3.1 M5, „proč“): the card of a board, the reference node of the tree, and on a page the block around the
/// link or mention with a short excerpt of its text (from the revision the use comes from: the working one for a draft's use). Asked
/// for one page of uses only: one statement per kind of place.
/// </summary>
public sealed class UsageReasons(ISession session)
{
    /// <param name="Card">The use is a contact card (vizitka), not a mention in the text.</param>
    public sealed record Reason(string? PartId, string? Excerpt, bool Card = false);

    private const string Ids = """CAST(string_to_array(:ids, ',') AS uuid[])""";

    public async Task<IReadOnlyDictionary<ObjectEdge, Reason>> ReadAsync(Guid objectId, IReadOnlyCollection<ObjectEdge> uses, CancellationToken ct)
    {
        var reasons = new Dictionary<ObjectEdge, Reason>();
        var pages = Places(uses, RelationKinds.Link, RelationKinds.Mention, RelationKinds.Party);
        if (pages.Length > 0)
        {
            var revisions = await session.ReadRowsAsync($"""
                SELECT r."PageId",r."Id" IS NOT DISTINCT FROM p."PublishedRevisionId",r."ContentJson" FROM "PageRevision" r JOIN "Page" p ON p."Id"=r."PageId"
                WHERE r."PageId"=ANY({Ids}) AND (r."Id"=p."PublishedRevisionId" OR r."ActiveTo" IS NULL)
                """, new Dictionary<string, object> { ["ids"] = string.Join(',', pages) }, row => (Page: row.GetGuid(0), Published: row.GetBoolean(1), Json: row.GetString(2)), ct);
            foreach (var edge in uses.Where(edge => edge.Kind is RelationKinds.Link or RelationKinds.Mention or RelationKinds.Party))
            {
                var revision = revisions.Where(row => row.Page == RelationKinds.PlaceOf(edge)).OrderBy(row => row.Published != !edge.Draft).FirstOrDefault();
                if (revision.Json is not null)
                {
                    reasons[edge] = UsageExcerpt.Find(revision.Json, objectId);
                }
            }
        }
        await PartsAsync(reasons, uses, RelationKinds.Placement, $"""
            SELECT "BoardId","Id" FROM "BoardItem" WHERE "BoardId"=ANY({Ids}) AND "TargetResourceId"=:target AND NOT "IsDeleted" ORDER BY "Z","Id"
            """, objectId, ct);
        await PartsAsync(reasons, uses, RelationKinds.Reference, $"""
            SELECT "ParentPageId","Id" FROM "TreeReference" WHERE "ParentPageId"=ANY({Ids}) AND "TargetId"=:target AND NOT "IsTrashed" ORDER BY "Id"
            """, objectId, ct);
        return reasons;
    }

    /// <summary>The first card (reference node) of each place: the one a jump opens.</summary>
    private async Task PartsAsync(Dictionary<ObjectEdge, Reason> reasons, IReadOnlyCollection<ObjectEdge> uses, string kind, string sql, Guid objectId, CancellationToken ct)
    {
        var places = Places(uses, kind);
        if (places.Length == 0)
        {
            return;
        }
        var parts = (await session.ReadRowsAsync(sql, new Dictionary<string, object> { ["ids"] = string.Join(',', places), ["target"] = objectId },
                row => (Place: row.GetGuid(0), Part: row.GetGuid(1)), ct))
            .GroupBy(row => row.Place).ToDictionary(group => group.Key, group => group.First().Part);
        foreach (var edge in uses.Where(edge => edge.Kind == kind))
        {
            if (parts.TryGetValue(RelationKinds.PlaceOf(edge), out var part))
            {
                reasons[edge] = new Reason(part.ToString(), null);
            }
        }
    }

    private static Guid[] Places(IEnumerable<ObjectEdge> uses, params string[] kinds) =>
        uses.Where(edge => kinds.Contains(edge.Kind)).Select(RelationKinds.PlaceOf).Distinct().ToArray();
}

/// <summary>The block of a stored rich document that names an object by its id (a page reference, a link, a contact's objectId).</summary>
public static class UsageExcerpt
{
    public const int Length = 160;

    /// <summary>The text blocks an excerpt is cut from; anything else (a table, a list) is searched for the deepest one inside.</summary>
    private static readonly HashSet<string> TextBlocks = new(StringComparer.Ordinal)
        { "paragraph", "heading", "listItem", "taskItem", "timedItem", "tableCell", "tableHeader", "blockquote", "codeBlock", "contactCard" };

    /// <summary>The block's id (attrs.blockId or attrs.id) and its text, or the section heading before it when it has none (a card). A use
    /// matched by e-mail names no id: nothing.</summary>
    public static UsageReasons.Reason Find(string contentJson, Guid objectId)
    {
        var needle = objectId.ToString("D");
        using var json = JsonDocument.Parse(contentJson, MarkdownDocumentValidator.JsonOptions);
        if (!json.RootElement.TryGetProperty("document", out var document) || !document.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
        {
            return new UsageReasons.Reason(null, null);
        }
        string? heading = null;
        foreach (var block in blocks.EnumerateArray())
        {
            if (Names(block, needle))
            {
                var holder = Innermost(block, needle) ?? block;
                if (TypeOf(holder) == "contactCard")
                {
                    // The card of this person only (its name and the page's role for it), after the section that says why it is there.
                    var card = CardOf(holder, needle) ?? Text(holder);
                    return new UsageReasons.Reason(Id(holder) ?? Id(block), Shorten(heading is null ? card : $"{heading}: {card}"), Card: true);
                }
                var text = Text(holder);
                return new UsageReasons.Reason(Id(holder) ?? Id(block), Shorten(text.Length > 0 ? text : heading));
            }
            if (TypeOf(block) == "heading")
            {
                heading = Text(block);
            }
        }
        return new UsageReasons.Reason(null, null);
    }

    private static bool Names(JsonElement node, string needle) => node.ValueKind switch
    {
        JsonValueKind.String => node.GetString()!.Contains(needle, StringComparison.OrdinalIgnoreCase),
        JsonValueKind.Object => node.EnumerateObject().Any(property => Names(property.Value, needle)),
        JsonValueKind.Array => node.EnumerateArray().Any(item => Names(item, needle)),
        _ => false
    };

    private static JsonElement? Innermost(JsonElement node, string needle)
    {
        if (node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in content.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.Object && Names(child, needle) && Innermost(child, needle) is { } deeper)
                {
                    return deeper;
                }
            }
        }
        return TextBlocks.Contains(TypeOf(node) ?? "") ? node : null;
    }

    /// <summary>The words a reader sees: text and a contact's name. Not a page reference's stored title: the referenced page may be one the
    /// reader (or MCP, which redacts such titles) may not see.</summary>
    private static string Text(JsonElement node)
    {
        var words = new List<string>();
        Collect(node);
        return string.Join(" ", words);

        void Collect(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                words.Add(text.GetString()!);
            }
            if (element.TryGetProperty("attrs", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                words.AddRange(ContactSnapshotNames(TypeOf(element), attrs));
            }
            if (element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in content.EnumerateArray())
                {
                    Collect(child);
                }
            }
        }
    }

    /// <summary>„Jméno · role“ of the card's contact with this object id; null when none of them has it (a use matched otherwise).</summary>
    private static string? CardOf(JsonElement card, string needle)
    {
        var contact = Atlas.Server.Platform.Content.ContactSnapshots.Of("contactCard", card.TryGetProperty("attrs", out var attrs) ? attrs : default)
            .FirstOrDefault(contact => contact.TryGetProperty("objectId", out var id) && id.ValueKind == JsonValueKind.String
                && string.Equals(id.GetString(), needle, StringComparison.OrdinalIgnoreCase));
        if (contact.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var role = contact.TryGetProperty("role", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
        var name = contact.GetProperty("name").GetString()!;
        return string.IsNullOrEmpty(role) ? name : $"{name} · {role}";
    }

    private static IEnumerable<string> ContactSnapshotNames(string? type, JsonElement attrs) =>
        Atlas.Server.Platform.Content.ContactSnapshots.Named(type, attrs).Select(contact => contact.GetProperty("name").GetString()!);

    private static string? TypeOf(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;

    private static string? Id(JsonElement node)
    {
        if (!node.TryGetProperty("attrs", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var name in new[] { "blockId", "id" })
        {
            if (attrs.TryGetProperty(name, out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 and <= 128 } value)
            {
                return value;
            }
        }
        return null;
    }

    private static string? Shorten(string? text)
    {
        var words = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return words.Length == 0 ? null : words.Length <= Length ? words : words[..(Length - 1)].TrimEnd() + "…";
    }
}
