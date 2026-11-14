using System.Text;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Markdown.Sync.Recognition;
using Atlas.Server.Features.PageLinks;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Platform.Linking;

/// <summary>
/// One reference a document holds, in document order (<see cref="Ordinal"/>): a chip or a link to a page (<c>linked</c>), a chip of
/// several people nobody picked (<c>unresolved</c>), or plain prose the recognition core knows (<c>suggested</c>), with the entity keys it
/// may be (<see cref="Candidates"/>), the heading above it (<see cref="Anchor"/>) and the recognizer (<c>kind.form</c>).
/// </summary>
public sealed class ContentOccurrence
{
    public required int Ordinal { get; init; }
    public required string Kind { get; init; }
    public required string State { get; init; }
    public Guid? TargetId { get; init; }
    public string? TargetKey { get; init; }
    public IReadOnlyList<string> Candidates { get; init; } = [];
    public required string Text { get; init; }
    public string? Anchor { get; init; }
    /// <summary>The block's text around the occurrence as a reader sees it (chips by their names), at most <see cref="ContentReferences.ContextLength"/>.</summary>
    public string? Context { get; init; }
    public string? Recognizer { get; init; }
    /// <summary>What the core decided for plain prose: a chip when it is one whole known name of one person.</summary>
    public RecognitionDecision Decision { get; init; }

    /// <summary>Plain prose inside one text node: where it is, so it can become a chip. Null when it spans several nodes.</summary>
    internal (JsonArray Parent, int Child, int Offset)? Place { get; init; }
    /// <summary>The chip itself (an unresolved one), so a pick or „Převést na text“ can rewrite it.</summary>
    internal (JsonArray Parent, int Child)? Chip { get; init; }
    /// <summary>A contact card's entry, so a pick can name its person and keep the card's details.</summary>
    internal JsonObject? Snapshot { get; init; }
}

/// <summary>
/// The references of a rich document (audit engine §5 <c>ContentReferences.Extract</c>): every chip of a person, page or issue and
/// every link to a page, and in paragraphs (prose: no headings, code or links) what the recognition core finds, as the editor would on
/// typing it. Also turns an occurrence into a chip or back to text in place. No I/O: the place's lexicon comes in.
/// </summary>
public static class ContentReferences
{
    /// <summary>The most rows of one source (P17); the rest waits for an edit of the page.</summary>
    public const int MaxRows = 2000;

    private const char NotProse = '￼';

    /// <summary>The most characters of <see cref="ContentOccurrence.Context"/>.</summary>
    public const int ContextLength = 160;

    /// <summary>The occurrences of a stored envelope (<c>{"document": …}</c>); the document is parsed here and kept by the occurrences.
    /// <paramref name="self"/>: the page it is, whose own title is not offered as a link to itself (as in the editor).</summary>
    public static (JsonNode Root, IReadOnlyList<ContentOccurrence> Occurrences) Read(string contentJson, PlaceLexicon lexicon, Guid? self = null)
    {
        var root = JsonNode.Parse(contentJson, documentOptions: MarkdownDocumentValidator.JsonOptions)!;
        // As the editor opens it: a 2.x import's repeated link mark would otherwise fail the validation of every linking write.
        if (root["document"] is JsonObject document)
        {
            LegacyDocumentRepair.Repair(document);
        }
        return (root, Read(root["document"], lexicon, self));
    }

    public static IReadOnlyList<ContentOccurrence> Read(JsonNode? document, PlaceLexicon lexicon, Guid? self = null)
    {
        var found = new List<ContentOccurrence>();
        string? anchor = null;
        Walk(document, lexicon, ref anchor, found, self is { } page ? "page:" + page : null);
        return found;
    }

    private static void Walk(JsonNode? node, PlaceLexicon lexicon, ref string? anchor, List<ContentOccurrence> found, string? self)
    {
        if (node is not JsonObject element || found.Count >= MaxRows)
        {
            return;
        }
        var type = RichDocument.TypeOf(element);
        if (type == "heading" && element["attrs"]?["headingId"] is JsonValue heading && heading.TryGetValue<string>(out var id))
        {
            anchor = id;
        }
        // A requirement heading's party (shape §6) is its contact, as the chip of the facts line was.
        if (type == "heading" && ContactSnapshots.Party(element["attrs"]) is { } party)
        {
            Add(found, Person(party, lexicon, anchor, null, 0));
        }
        if (type is "codeBlock" or "mathBlock" or "diagram")
        {
            return;
        }
        if (type == "contactCard")
        {
            foreach (var contact in Contacts(element))
            {
                Add(found, Person(contact, lexicon, anchor, null, 0, contact));
            }
            return;
        }
        if (element["content"] is not JsonArray content)
        {
            return;
        }
        if (type is "paragraph" or "heading")
        {
            Block(content, type == "paragraph", lexicon, anchor, found, self);
            return;
        }
        foreach (var child in content.ToArray())
        {
            Walk(child, lexicon, ref anchor, found, self);
        }
    }

    /// <summary>A textblock: its chips and page links, and in prose the core's hits, merged by position.</summary>
    private static void Block(JsonArray content, bool prose, PlaceLexicon lexicon, string? anchor, List<ContentOccurrence> found, string? self)
    {
        var text = new StringBuilder();
        // What a reader sees: the text with each chip's name for its one character, and where those names lengthen it.
        var shown = new StringBuilder();
        var longer = new List<(int At, int By)>();
        var segments = new List<(int Start, int Length, int Child)>();
        var items = new List<(int At, int End, ContentOccurrence Occurrence)>();
        string? linkHref = null;
        for (var i = 0; i < content.Count; i++)
        {
            var at = text.Length;
            if (content[i] is not JsonObject inline)
            {
                continue;
            }
            var type = RichDocument.TypeOf(inline);
            if (type == "text")
            {
                var value = inline["text"]?.GetValue<string>() ?? "";
                var marks = inline["marks"] as JsonArray;
                var href = marks?.OfType<JsonObject>().FirstOrDefault(mark => RichDocument.TypeOf(mark) == "link")?["attrs"]?["href"]?.GetValue<string>();
                var code = marks?.OfType<JsonObject>().Any(mark => RichDocument.TypeOf(mark) == "code") == true;
                if (PageLinkTargets.OfHref(href) is { } page)
                {
                    // One link split by formatting („**Provozní** příručka“) is one reference.
                    if (href != linkHref)
                    {
                        items.Add((at, at + value.Length, new ContentOccurrence
                        {
                            Ordinal = 0, Kind = "page", State = "linked", TargetId = page, TargetKey = "page:" + page, Text = Clip(value), Anchor = anchor,
                        }));
                    }
                }
                linkHref = href;
                shown.Append(value);
                if (href is not null || code)
                {
                    text.Append(NotProse, value.Length);
                }
                else
                {
                    segments.Add((at, value.Length, i));
                    text.Append(value);
                }
                continue;
            }
            linkHref = null;
            text.Append(NotProse);
            var label = type == "hardBreak" ? " " : Contacts(inline).FirstOrDefault()?["name"]?.GetValue<string>()
                ?? inline["attrs"]?["title"]?.GetValue<string>() ?? IssueKey(inline) ?? "";
            shown.Append(label);
            longer.Add((at, label.Length - 1));
            var chip = (content, i);
            switch (type)
            {
                case "contactInline":
                    foreach (var contact in Contacts(inline).Take(1))
                    {
                        items.Add((at, at + 1, Person(contact, lexicon, anchor, chip, 0)));
                    }
                    break;
                case "pageReference" when PageLinkTargets.TryUuid(inline["attrs"]?["id"]?.GetValue<string>(), out var page):
                    items.Add((at, at + 1, new ContentOccurrence
                    {
                        Ordinal = 0, Kind = "page", State = "linked", TargetId = page, TargetKey = "page:" + page,
                        Text = Clip(inline["attrs"]?["title"]?.GetValue<string>() ?? ""), Anchor = anchor,
                    }));
                    break;
                case "youtrackIssue" when IssueKey(inline) is { } issue:
                    items.Add((at, at + 1, new ContentOccurrence { Ordinal = 0, Kind = "issue", State = "linked", TargetKey = "issue:" + issue, Text = Clip(issue), Anchor = anchor }));
                    break;
            }
        }
        if (prose)
        {
            var value = text.ToString();
            foreach (var hit in lexicon.Index.RecognizeBlock(value, lexicon.Lexicon.YouTrackProjects, 100))
            {
                var candidates = hit.Kind == "issue" ? ["issue:" + hit.Text.TrimStart('#')] : hit.Candidates.Select(PlaceLexicon.KeyOf).Where(key => key != self).ToArray();
                if (hit.Kind == "page" && candidates.Length == 0)
                {
                    continue;
                }
                var segment = segments.FirstOrDefault(segment => segment.Start <= hit.From && hit.From < segment.Start + segment.Length);
                items.Add((hit.From, hit.To, new ContentOccurrence
                {
                    Ordinal = 0, Kind = hit.Kind, State = "suggested", Text = Clip(hit.Text), Anchor = anchor, Decision = hit.Decision,
                    Recognizer = $"{hit.Kind}.{hit.Form}",
                    Candidates = candidates,
                    Place = segment.Length > 0 && hit.To <= segment.Start + segment.Length ? (content, segment.Child, hit.From - segment.Start) : null,
                }));
            }
        }
        var reader = shown.ToString();
        int Shown(int at) => at + longer.Where(chip => chip.At < at).Sum(chip => chip.By);
        foreach (var (from, to, occurrence) in items.OrderBy(item => item.At))
        {
            Add(found, occurrence, Context(reader, Shown(from), Shown(to)));
        }
    }

    /// <summary>At most <see cref="ContextLength"/> characters of <paramref name="text"/> around <paramref name="from"/>..<paramref name="to"/>,
    /// cut at spaces, with „…“ where it is cut.</summary>
    private static string Context(string text, int from, int to)
    {
        var start = Math.Max(0, from - Math.Max(0, ContextLength - (to - from)) / 2);
        var end = Math.Min(text.Length, start + ContextLength);
        start = Math.Max(0, Math.Min(start, end - ContextLength));
        if (start > 0 && text.IndexOf(' ', start, from - start) is var space and >= 0)
        {
            start = space + 1;
        }
        if (end < text.Length && text.LastIndexOf(' ', end - 1, end - to) is var last and >= 0)
        {
            end = last;
        }
        return (start > 0 ? "…" : "") + text[start..end].Trim() + (end < text.Length ? "…" : "");
    }

    private static void Add(List<ContentOccurrence> found, ContentOccurrence occurrence, string? context = null)
    {
        if (found.Count < MaxRows)
        {
            found.Add(new ContentOccurrence
            {
                Ordinal = found.Count, Kind = occurrence.Kind, State = occurrence.State, TargetId = occurrence.TargetId, TargetKey = occurrence.TargetKey,
                Candidates = occurrence.Candidates, Text = occurrence.Text, Anchor = occurrence.Anchor, Recognizer = occurrence.Recognizer,
                Decision = occurrence.Decision, Place = occurrence.Place, Chip = occurrence.Chip, Snapshot = occurrence.Snapshot, Context = context,
            });
        }
    }

    /// <summary>A contact snapshot: linked by its directory object or account, else an unresolved name with the people it may be.</summary>
    private static ContentOccurrence Person(JsonObject contact, PlaceLexicon lexicon, string? anchor, (JsonArray, int)? chip, int ordinal, JsonObject? snapshot = null)
    {
        var name = contact["name"]?.GetValue<string>() ?? "";
        if (Guid.TryParse(contact["objectId"]?.GetValue<string>(), out var objectId))
        {
            return new ContentOccurrence { Ordinal = ordinal, Kind = "person", State = "linked", TargetId = objectId, TargetKey = "object:" + objectId, Text = Clip(name), Anchor = anchor, Chip = chip };
        }
        if (!ContactSnapshots.IsUnresolved(contact) && Guid.TryParse(contact["internalUser"]?["id"]?.GetValue<string>(), out var user))
        {
            return new ContentOccurrence { Ordinal = ordinal, Kind = "person", State = "linked", TargetId = user, TargetKey = "user:" + user, Text = Clip(name), Anchor = anchor, Chip = chip };
        }
        // A place of a template („PM“) or a name nobody has: no candidates; the pick names the person.
        var whole = lexicon.Index.RecognizeText(name).FirstOrDefault(hit => hit.Kind == "person" && hit.From == 0 && hit.To == name.Length);
        return new ContentOccurrence
        {
            Ordinal = ordinal, Kind = "person", State = "unresolved", Text = Clip(name), Anchor = anchor, Chip = chip, Snapshot = snapshot, Recognizer = "person.chip",
            Candidates = whole?.Candidates.Select(PlaceLexicon.KeyOf).ToArray() ?? [],
        };
    }

    private static IEnumerable<JsonObject> Contacts(JsonObject node) =>
        (node["attrs"]?["data"]?["contacts"] as JsonArray)?.OfType<JsonObject>().Where(contact => contact["name"] is JsonValue) ?? [];

    private static string? IssueKey(JsonObject node) => node["attrs"]?["key"]?.GetValue<string>() is { Length: > 0 } key ? key : null;

    private static string Clip(string text) => text.Length <= 200 ? text : text[..200];

    /// <summary>
    /// Turns <paramref name="occurrence"/> into <paramref name="chip"/>: plain prose in one text node is split around it (the chip takes
    /// the node's marks), a chip is replaced. Apply several in descending <see cref="ContentOccurrence.Ordinal"/>: earlier places stay valid.
    /// </summary>
    public static bool Link(ContentOccurrence occurrence, JsonObject chip)
    {
        if (occurrence.Snapshot is { } snapshot)
        {
            // A card's entry keeps its phones and e-mails: the pick only says which person it is.
            if (Contacts(chip).FirstOrDefault() is not { } person)
            {
                return false;
            }
            snapshot.Remove("unresolved");
            foreach (var key in new[] { "objectId", "internalUser" })
            {
                snapshot.Remove(key);
                if (person[key] is { } value)
                {
                    snapshot[key] = value.DeepClone();
                }
            }
            return true;
        }
        if (occurrence.Chip is var (chipParent, chipChild))
        {
            var marks = chipParent[chipChild]?["marks"]?.DeepClone();
            if (marks is not null)
            {
                chip["marks"] = marks;
            }
            chipParent[chipChild] = chip;
            return true;
        }
        if (occurrence.Place is not var (parent, child, offset) || parent[child] is not JsonObject node || node["text"]?.GetValue<string>() is not { } text)
        {
            return false;
        }
        var length = occurrence.Text.Length;
        if (offset + length > text.Length)
        {
            return false;
        }
        if (node["marks"] is { } nodeMarks)
        {
            chip["marks"] = nodeMarks.DeepClone();
        }
        var parts = new List<JsonNode>();
        if (offset > 0)
        {
            parts.Add(WithText(node, text[..offset]));
        }
        parts.Add(chip);
        if (offset + length < text.Length)
        {
            parts.Add(WithText(node, text[(offset + length)..]));
        }
        parent.RemoveAt(child);
        for (var i = 0; i < parts.Count; i++)
        {
            parent.Insert(child + i, parts[i]);
        }
        return true;
    }

    /// <summary>An unresolved chip back to its text („Převést na text“), with the chip's marks.</summary>
    public static bool ToText(ContentOccurrence occurrence)
    {
        if (occurrence.State != "unresolved" || occurrence.Chip is not var (parent, child) || parent[child] is not JsonObject chip)
        {
            return false;
        }
        var text = new JsonObject { ["type"] = "text", ["text"] = Contacts(chip).FirstOrDefault()?["name"]?.GetValue<string>() ?? occurrence.Text };
        if (chip["marks"] is { } marks)
        {
            text["marks"] = marks.DeepClone();
        }
        parent[child] = text;
        return true;
    }

    private static JsonObject WithText(JsonObject node, string text)
    {
        var copy = (JsonObject)node.DeepClone();
        copy["text"] = text;
        return copy;
    }

    /// <summary>The chip of a person: a directory contact by its id, an account without one by the account (the save links it, R3).</summary>
    public static JsonObject PersonChip(PlaceEntity person) => new()
    {
        ["type"] = "contactInline",
        ["attrs"] = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["version"] = 1,
                ["contacts"] = new JsonArray(person.Key.StartsWith("object:", StringComparison.Ordinal)
                    ? new JsonObject { ["name"] = person.Label, ["objectId"] = person.Id.ToString() }
                    : new JsonObject { ["name"] = person.Label, ["internalUser"] = new JsonObject { ["id"] = person.Id.ToString(), ["name"] = person.Label } }),
            },
        },
    };

    /// <summary>The chip of several people of one name, nobody picked yet.</summary>
    public static JsonObject UnresolvedChip(string name) => new()
    {
        ["type"] = "contactInline",
        ["attrs"] = new JsonObject { ["data"] = new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(new JsonObject { ["name"] = name, ["unresolved"] = true }) } },
    };

    public static JsonObject PageChip(PlaceEntity page) => new()
    {
        ["type"] = "pageReference",
        ["attrs"] = new JsonObject { ["id"] = page.Id.ToString(), ["title"] = page.Label, ["url"] = "#/pages/" + page.Id },
    };

    /// <summary>The envelope after chips were made in it: their plugins declared (contacts, pages), as a Markdown write's merge does.</summary>
    public static string Write(JsonNode root)
    {
        if (root is JsonObject envelope && envelope["document"] is JsonObject document)
        {
            RichMarkdownMerge.DeclarePlugins(envelope, document);
        }
        return root.ToJsonString(RichDocument.JsonWrite);
    }
}
