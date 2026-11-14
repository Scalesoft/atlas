using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Atlas.Markdown.Sync;

/// <summary>One repair <see cref="LegacyDocumentRepair"/> made: where (a JSON path as validation names it), what
/// (<see cref="LegacyDocumentRepair.RepeatedMark"/>, <see cref="LegacyDocumentRepair.LeadingParagraph"/>,
/// <see cref="LegacyDocumentRepair.SplitCard"/>, <see cref="LegacyDocumentRepair.SummaryOverview"/> or
/// <see cref="LegacyDocumentRepair.HistoryDateList"/>) and, for a removed
/// mark, the mark's JSON, so nothing the reader could act on is lost without a trace.</summary>
public sealed record LegacyDocumentChange(string Path, string Code, string? Removed = null);

/// <summary>
/// The content migrations: shapes older Atlas stored that the editor no longer writes, as the editor applies them when it opens a
/// document (@atlas/markdown engine/legacyDocument.ts, the same rules):
/// <list type="bullet">
/// <item>a mark repeated on one text node (a link nested in a link) keeps its last, innermost copy, the one 2.x rendered innermost
/// and a click opened;</item>
/// <item>a list or task item that is empty or starts with another block gets an empty leading paragraph;</item>
/// <item>one contact per block (owner 2026-10-30, Atlas 3.2): a <c>contactCard</c> of several contacts inside a <c>content</c> list
/// becomes as many consecutive single-contact cards, in order, each a copy with one contact (a card without a parent, a board's one
/// block, stays as it is).</item>
/// <item>one overview block (plan 3.4 R6): Atlas's deployment summary <c>requirementSummary</c> becomes the page overview
/// <c>overview</c> of kind <c>requirements</c>, its id kept.</item>
/// <item>a history line (Atlas 3.5.1) the MCP help wrote as <c>- 30. 10. 2026 — text</c> and the server read as two nested ordered lists
/// (start 30 › start 10 › „2026 — text“) is one paragraph „30. 10. 2026 — text“ again, its marks kept; only inside a <c>requirement</c>,
/// other nested lists stay.</item>
/// <item>a timed list (Atlas 3.6, rich revision 5): a <c>bulletList</c> whose first item starts with a stamp „3. 11. 2026 17:19 — “ is a
/// <c>timedList</c> of <c>timedItem</c>s, on any page, its attributes and Markdown unchanged.</item>
/// <item>no disclosure block (Atlas 3.6, rich revision 7: every heading folds): a <c>disclosure</c> is a heading carrying its title, then
/// its body, in place; nested ones too. Its title is its first non-empty <c>disclosureTitle</c> (revision 6), else its <c>title</c>
/// attribute (before), else „Podrobnosti“; the heading is one level below the nearest top-level heading before it (2–6), 3 with none (a
/// heading in a cell, list item or callout does not count). Its body takes that level, the next block the one before again: consecutive
/// disclosures stay siblings, a nested one goes below its parent.</item>
/// <item>link attributes (Atlas 4.0): a <c>target</c> other than <c>_blank</c>, a <c>rel</c> other than <c>noopener noreferrer</c> and any
/// <c>class</c> are dropped (the attribute takes its default), as the editor renders none of them.</item>
/// </list>
/// Hosts run it where stored content is read or enters (RichDocument, an archive import); every write still validates the result.
/// </summary>
public static partial class LegacyDocumentRepair
{
    public const string RepeatedMark = "repeated_mark";
    public const string LeadingParagraph = "leading_paragraph";
    public const string SplitCard = "split_contact_card";
    public const string SummaryOverview = "summary_overview";
    public const string HistoryDateList = "history_date_list";
    public const string TimedList = "timed_list";
    public const string DisclosureHeading = "disclosure_heading";
    public const string LinkAttributes = "link_attributes";

    /// <summary>Nodes whose content expression starts with a paragraph ("paragraph block*").</summary>
    // Raw UTF-8 and compact, as the editor and every host write stored rich JSON.
    private static readonly JsonSerializerOptions Write = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = MarkdownDocumentValidator.JsonMaxDepth };

    private static readonly HashSet<string> StartsWithParagraph = new(StringComparer.Ordinal) { "listItem", "taskItem", "timedItem" };

    /// <summary>The repaired envelope, or <paramref name="json"/> itself when nothing changes or it is not a readable envelope
    /// (validation then reports it). <paramref name="changes"/> receives every repair.</summary>
    public static string Repair(string json, ICollection<LegacyDocumentChange>? changes = null)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: MarkdownDocumentValidator.JsonOptions);
        }
        catch (JsonException)
        {
            return json;
        }
        if ((root as JsonObject)?["document"] is not JsonObject document)
        {
            return json;
        }
        return Repair(document, changes) ? root.ToJsonString(Write) : json;
    }

    /// <summary>Repairs an envelope's parsed <c>document</c> in place, for a host that already holds it parsed; true when anything changed.</summary>
    public static bool Repair(JsonObject document, ICollection<LegacyDocumentChange>? changes = null)
    {
        var made = new List<LegacyDocumentChange>();
        Repair(document, "document", 0, false, made, new Walk());
        foreach (var change in made)
        {
            changes?.Add(change);
        }
        return made.Count > 0;
    }

    /// <summary>The level of the last top-level heading seen so far, in document order (a disclosure's heading goes one below it).</summary>
    private sealed class Walk
    {
        public int? Level;
    }

    private static void Repair(JsonObject node, string path, int depth, bool requirement, List<LegacyDocumentChange> changes, Walk walk)
    {
        // The validator refuses deeper documents; the bound keeps the recursion safe.
        if (depth > MarkdownDocumentValidator.MaxDepth)
        {
            return;
        }
        if (node["marks"] is JsonArray marks)
        {
            for (var i = marks.Count - 1; i >= 0; i--)
            {
                var type = Type(marks[i]);
                if (type is not null && marks.Skip(i + 1).Any(later => Type(later) == type))
                {
                    changes.Add(new LegacyDocumentChange($"{path}.marks[{i}]", RepeatedMark, marks[i]!.ToJsonString()));
                    marks.RemoveAt(i);
                }
            }
            for (var i = 0; i < marks.Count; i++)
            {
                if (Type(marks[i]) == "link" && marks[i]!["attrs"] is JsonObject attrs && DropLinkAttributes(attrs))
                {
                    changes.Add(new LegacyDocumentChange($"{path}.marks[{i}]", LinkAttributes));
                }
            }
        }
        if (node["content"] is JsonArray content)
        {
            requirement |= Type(node) == "requirement";
            Blocks(content, path, depth, requirement, changes, walk, Type(node) == "doc");
            if (requirement && StartsWithParagraph.Contains(Type(node) ?? "") && content.Count > 1 && EmptyParagraph(content[0]) && HistoryLine(content[1]) is not null)
            {
                // The empty paragraph a list item got because it started with the day's list.
                content.RemoveAt(0);
            }
            for (var i = content.Count - 1; requirement && i >= 0; i--)
            {
                if (HistoryLine(content[i]) is { } line)
                {
                    content.RemoveAt(i);
                    for (var at = 0; at < line.Count; at++)
                    {
                        content.Insert(i + at, line[at]);
                    }
                    changes.Add(new LegacyDocumentChange($"{path}.content[{i}]", HistoryDateList));
                }
            }
            for (var i = content.Count - 1; i >= 0; i--)
            {
                if (Type(content[i]) == "contactCard" && content[i]!["attrs"]?["data"]?["contacts"] is JsonArray { Count: > 1 } contacts)
                {
                    var card = content[i]!;
                    content.RemoveAt(i);
                    for (var at = 0; at < contacts.Count; at++)
                    {
                        var single = card.DeepClone();
                        single["attrs"]!["data"]!["contacts"] = new JsonArray(contacts[at]?.DeepClone());
                        content.Insert(i + at, single);
                    }
                    changes.Add(new LegacyDocumentChange($"{path}.content[{i}]", SplitCard));
                }
            }
        }
        if (Type(node) == "requirementSummary")
        {
            node["type"] = "overview";
            var attrs = node["attrs"] as JsonObject ?? [];
            attrs["kind"] = "requirements";
            node["attrs"] = attrs;
            changes.Add(new LegacyDocumentChange(path, SummaryOverview));
        }
        if (Type(node) == "bulletList" && node["content"] is JsonArray { Count: > 0 } entries && Stamped(entries[0]))
        {
            node["type"] = "timedList";
            foreach (var item in entries.OfType<JsonObject>().Where(x => Type(x) == "listItem"))
            {
                item["type"] = "timedItem";
            }
            changes.Add(new LegacyDocumentChange(path, TimedList));
        }
        if (Type(node) is { } name && StartsWithParagraph.Contains(name) && (node["content"] is null or JsonArray)
            && Type((node["content"] as JsonArray)?.FirstOrDefault()) != "paragraph")
        {
            var items = node["content"] as JsonArray ?? [];
            items.Insert(0, new JsonObject { ["type"] = "paragraph" });
            node["content"] = items;
            changes.Add(new LegacyDocumentChange(path + ".content", LeadingParagraph));
        }
    }

    /// <summary>Repairs <paramref name="content"/> in document order, each disclosure among it a heading and its body (its nested
    /// disclosures too, the body repaired before it is spliced in). <paramref name="top"/>: top-level blocks, whose headings set the level.</summary>
    private static void Blocks(JsonArray content, string path, int depth, bool requirement, List<LegacyDocumentChange> changes, Walk walk, bool top)
    {
        for (var i = 0; i < content.Count; i++)
        {
            if (Type(content[i]) == "disclosure" && content[i]!["content"] is JsonArray inner)
            {
                var before = walk.Level;
                var level = Math.Clamp(before is { } above ? above + 1 : 3, 2, 6);
                var title = inner.FirstOrDefault(x => Type(x) == "disclosureTitle" && x!["content"] is JsonArray { Count: > 0 })?["content"]?.DeepClone()
                    ?? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = content[i]!["attrs"]?["title"] is JsonValue value
                        && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : "Podrobnosti" });
                var body = new JsonArray(inner.Where(x => Type(x) != "disclosureTitle").Select(x => x!.DeepClone()).ToArray());
                changes.Add(new LegacyDocumentChange($"{path}.content[{i}]", DisclosureHeading));
                walk.Level = level;
                Blocks(body, $"{path}.content[{i}]", depth + 1, requirement, changes, walk, top);
                walk.Level = before;
                // In place: its heading here, its repaired body after it.
                content[i] = new JsonObject { ["type"] = "heading", ["attrs"] = new JsonObject { ["level"] = level }, ["content"] = title };
                var blocks = body.ToList();
                body.Clear();
                for (var at = 0; at < blocks.Count; at++)
                {
                    content.Insert(i + 1 + at, blocks[at]);
                }
                i += blocks.Count;
                continue;
            }
            if (content[i] is JsonObject child)
            {
                if (top && Type(child) == "heading" && child["attrs"]?["level"] is JsonValue heading && heading.TryGetValue<int>(out var headingLevel))
                {
                    walk.Level = headingLevel;
                }
                Repair(child, $"{path}.content[{i}]", depth + 1, requirement, changes, walk);
            }
        }
    }

    /// <summary>The blocks a history line read as nested ordered lists stands for, or null: <paramref name="list"/> is an
    /// <c>orderedList</c> starting at a day (1–31) with one item that holds, after its empty leading paragraph, an <c>orderedList</c>
    /// starting at a month (1–12) with one item whose first paragraph starts with a four-digit year. That paragraph gets „D. M. “ in front
    /// (a text of its own before a marked first text); the blocks after it in both items follow it.</summary>
    private static List<JsonNode>? HistoryLine(JsonNode? list)
    {
        if (DateItem(list, 31) is not { } day || ItemBlocks(day) is not [var months, .. var after] || DateItem(months, 12) is not { } month
            || ItemBlocks(month) is not [JsonObject { } first, .. var rest] || Type(first) != "paragraph"
            || first["content"] is not JsonArray { Count: > 0 } inlines || Type(inlines[0]) != "text"
            || inlines[0]!["text"] is not JsonValue text || !text.TryGetValue<string>(out var value) || !Year().IsMatch(value))
        {
            return null;
        }
        var date = $"{Start(list)}. {Start(months)}. ";
        var paragraph = first.DeepClone().AsObject();
        var content = paragraph["content"]!.AsArray();
        if (content[0]!["marks"] is JsonArray { Count: > 0 })
        {
            content.Insert(0, new JsonObject { ["type"] = "text", ["text"] = date });
        }
        else
        {
            content[0]!["text"] = date + value;
        }
        return [paragraph, .. rest.Concat(after).Select(x => x!.DeepClone())];
    }

    /// <summary>The single <c>listItem</c> of an <c>orderedList</c> whose start is 1 to <paramref name="max"/>, or null.</summary>
    private static JsonObject? DateItem(JsonNode? list, int max) =>
        Type(list) == "orderedList" && Start(list) is >= 1 and var start && start <= max && list!["content"] is JsonArray { Count: 1 } items
        && Type(items[0]) == "listItem" ? (JsonObject)items[0]! : null;

    private static int Start(JsonNode? list) =>
        list?["attrs"]?["start"] is JsonValue value && value.TryGetValue<int>(out var start) ? start : 1;

    /// <summary>An item's blocks without its empty leading paragraph.</summary>
    private static JsonNode?[] ItemBlocks(JsonObject item) =>
        item["content"] is JsonArray content ? [.. content.Skip(content.Count > 0 && EmptyParagraph(content[0]) ? 1 : 0)] : [];

    private static bool EmptyParagraph(JsonNode? node) =>
        Type(node) == "paragraph" && node!["content"] is null or JsonArray { Count: 0 };

    [GeneratedRegex(@"^\d{4}(?!\d)")]
    private static partial Regex Year();

    // A timed item's stamp (@atlas/markdown features/timed/timedList.ts STAMP): the rest may be empty.
    [GeneratedRegex(@"^[0-9]{1,2}\.\s*[0-9]{1,2}\.\s*[0-9]{4}(?:\s+[0-9]{1,2}:[0-9]{2})?\s+[—–-](?:\s+|\z)")]
    private static partial Regex Stamp();

    /// <summary>A <c>listItem</c> whose first paragraph's text (its text nodes) starts with a stamp: the first item of a timed list (MarkdownToRich too).</summary>
    public static bool Stamped(JsonNode? item) =>
        Type(item) == "listItem" && item!["content"] is JsonArray { Count: > 0 } blocks && Type(blocks[0]) == "paragraph"
        && blocks[0]!["content"] is JsonArray inlines
        && Stamp().IsMatch(string.Concat(inlines.Select(x => Type(x) == "text" && x!["text"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "")));

    /// <summary>Drops a link's target but <c>_blank</c>, rel but <c>noopener noreferrer</c> and any class; true when one went.</summary>
    private static bool DropLinkAttributes(JsonObject attrs)
    {
        var dropped = false;
        foreach (var (name, kept) in new (string, string?)[] { ("target", "_blank"), ("rel", "noopener noreferrer"), ("class", null) })
        {
            // A JSON null reads as null here, like an absent attribute: both are kept.
            if (attrs[name] is { } value && (kept is null || value is not JsonValue text || !text.TryGetValue<string>(out var word) || word != kept))
            {
                attrs.Remove(name);
                dropped = true;
            }
        }
        return dropped;
    }

    private static string? Type(JsonNode? node) =>
        (node as JsonObject)?["type"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
