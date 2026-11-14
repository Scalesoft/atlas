using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Projector.Domain;

namespace Atlas.Server.Platform.Content;

/// <summary>A converted envelope (the same string when nothing changed) and its report.</summary>
public sealed record RequirementConversion(string Json, bool Changed, RequirementConversionReport Report);

/// <summary>
/// The 3.6 conversion of old <c>requirement</c> blocks to requirement headings (plan 2026-11-04 §5, model §10b), pure: one stored
/// envelope in, the converted envelope and its report out. A block becomes a heading one level under the nearest plain heading above
/// (at most h6) carrying its state, kind, own state word, service and party (§6), its old id and key in <c>aliases</c>; then its section:
/// the dependency line, the facts line (the deadline), a „Chybějící prerekvizita: klíč“ line for each key no block holds, „Kde uloženo: …“,
/// and the block's own content as it was. A block holding blocks becomes a plain heading with its notes, a „Dříve požadavek: …“ line for
/// what it set, and its children one level under it. A version-3 page (<c>atlas-requirements</c> declared below
/// <see cref="RequirementHeadings.PartyVersion"/>) has its requirement headings' party moved into their attributes first
/// (<see cref="RequirementHeadings.MoveParty"/>, shape §6), compared with what its facts lines said. A document without blocks and not of
/// version 3 comes back unchanged.
/// </summary>
public static partial class RequirementHeadingConverter
{
    public const int MaxLevel = 6;
    private const string Plugin = "atlas-requirements";

    /// <summary>Whether content is in an old shape (a requirement block, or <c>atlas-requirements</c> declared below
    /// <see cref="RequirementHeadings.PartyVersion"/>), read from the parsed JSON whatever its spacing; unreadable JSON is not.</summary>
    public static bool OldShape(string? json)
    {
        if (json is null || !json.Contains("requirement", StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            return JsonNode.Parse(json, documentOptions: MarkdownDocumentValidator.JsonOptions) is JsonObject root
                && (Legacy(root) || root["document"] is JsonObject document && Blocks(document).Any());
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Whether the document declares <c>atlas-requirements</c> below <see cref="RequirementHeadings.PartyVersion"/> (version 3: the party in the facts line).</summary>
    private static bool Legacy(JsonObject root)
        => (root["plugins"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(x => x["id"] is JsonValue name && name.TryGetValue<string>(out var id) && id == Plugin)
            ?["version"] is JsonValue declared && declared.TryGetValue<int>(out var number) && number < RequirementHeadings.PartyVersion;

    /// <param name="catalog">The catalog pages of the page's space in catalog order (the one marked first first): a block whose key one
    /// of them defines gets that definition as its <c>from</c> (model §10b, „catalog key“).</param>
    public static RequirementConversion Convert(string json, Guid pageId, RequirementKeyMap map, IReadOnlyList<Guid>? catalog = null)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: MarkdownDocumentValidator.JsonOptions) as JsonObject;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return new(json, false, RequirementConversionReport.Compare(pageId, [], [], [new(RequirementCaseKind.Unreadable, "", "")]));
        }
        if (root?["document"] is not JsonObject document || document["content"] is not JsonArray content)
        {
            return new(json, false, RequirementConversionReport.Compare(pageId, [], [], []));
        }
        var legacy = Legacy(root);
        var hasBlocks = RequirementHeadingConverter.Blocks(document).Any();
        if (!hasBlocks && !legacy)
        {
            var facts = RequirementHeadingReader.Read(pageId, document);
            return new(json, false, RequirementConversionReport.Compare(pageId, facts, facts, []));
        }
        // The page's requirement headings as they read before (a version-3 facts line), then the move of their party (§6).
        var before = RequirementHeadingReader.Read(pageId, document, legacy).ToList();
        var cases = new List<RequirementCase>();
        if (legacy)
        {
            var kept = new List<JsonNode>();
            RequirementHeadings.MoveParty(content, kept);
            cases.AddRange(kept.Select(heading => new RequirementCase(RequirementCaseKind.FactsLineKept, RequirementHeadings.HeadingId(heading), "")));
        }
        if (hasBlocks)
        {
            var run = new Run(pageId, map, document, catalog ?? []);
            var converted = run.Convert(content);
            // A block's own content still hangs in the old block: copied out.
            document["content"] = new JsonArray([.. converted.Select(node => node.Parent is null ? node : node.DeepClone())]);
            // A block the walk does not reach (inside a container) stays and is reported: the gate fails on it.
            foreach (var left in RequirementHeadingConverter.Blocks(document))
            {
                var id = RequirementText.Value(left, "id");
                run.Cases.Add(new(RequirementCaseKind.NotConverted, id, ""));
                run.Before.Add(run.Facts((JsonObject)left, id, []));
            }
            before.AddRange(run.Before);
            cases.AddRange(run.Cases);
        }
        RichMarkdownMerge.DeclarePlugins(root, document);
        foreach (var declaration in (root["plugins"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (declaration["id"] is JsonValue name && name.TryGetValue<string>(out var id) && id == Plugin && declaration["version"] is JsonValue version
                && version.TryGetValue<int>(out var current) && current < CollaborationSchema.CurrentPluginVersions[Plugin])
            {
                declaration["version"] = CollaborationSchema.CurrentPluginVersions[Plugin];
            }
        }
        var after = RequirementHeadingReader.Read(pageId, document);
        return new(root.ToJsonString(RichDocument.JsonWrite), true, RequirementConversionReport.Compare(pageId, before, after, cases));
    }

    private sealed class Run(Guid pageId, RequirementKeyMap map, JsonObject document, IReadOnlyList<Guid> catalog)
    {
        public readonly List<RequirementFacts> Before = [];
        public readonly List<RequirementCase> Cases = [.. map.Duplicates.Where(x => x.PageId == pageId)
            .Select(x => new RequirementCase(RequirementCaseKind.DuplicateKey, x.BlockIds[^1], string.Create(CultureInfo.InvariantCulture, $"{x.BlockIds.Count} blocks"))) ];
        private readonly HashSet<string> taken = (document["content"] as JsonArray ?? []).Where(x => RichDocument.TypeOf(x) == "heading")
            .Select(x => RequirementText.Value(x, "headingId")).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, string> names = RequirementText.DependencyNames(document);
        // The page's own keys resolve within this revision (a key only the other revision holds is missing here); the map gives their ids.
        private readonly Dictionary<string, JsonNode> ownKeys = RequirementHeadingConverter.Blocks(document).Where(x => RequirementText.Value(x, "key").Length > 0)
            .GroupBy(x => RequirementText.Value(x, "key"), StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);

        private readonly Dictionary<string, string> ids = new(StringComparer.Ordinal);

        public List<JsonNode> Convert(JsonArray content)
        {
            // Every id first, so a dependency names the id its target ends up with.
            foreach (var block in RequirementHeadingConverter.Blocks(document))
            {
                var oldId = RequirementText.Value(block, "id");
                if (!ids.ContainsKey(oldId))
                {
                    ids[oldId] = Assign((JsonObject)block, oldId);
                }
            }
            var output = new List<JsonNode>();
            var plain = 0;
            string? last = null;
            foreach (var node in content.OfType<JsonNode>().Select(x => x.DeepClone()))
            {
                switch (RichDocument.TypeOf(node))
                {
                    case "heading":
                        if (!RequirementHeadingReader.IsRequirementHeading(node))
                        {
                            plain = node["attrs"]?["level"] is JsonValue level && level.TryGetValue<int>(out var number) ? number : 1;
                        }
                        last = null;
                        output.Add(node);
                        break;
                    case "requirement":
                        last = Block((JsonObject)node, plain + 1, output);
                        break;
                    default:
                        if (last is not null)
                        {
                            Cases.Add(new(RequirementCaseKind.LooseContent, last, RichDocument.TypeOf(node)));
                        }
                        output.Add(node);
                        break;
                }
            }
            return output;
        }

        /// <summary>Writes the block at <paramref name="level"/>; returns the old id of the last requirement written (a nested block's last child).</summary>
        private string Block(JsonObject block, int level, List<JsonNode> output)
        {
            var oldId = RequirementText.Value(block, "id");
            if (level > MaxLevel)
            {
                Cases.Add(new(RequirementCaseKind.LevelCap, oldId, string.Create(CultureInfo.InvariantCulture, $"h{level}")));
                level = MaxLevel;
            }
            var id = ids[oldId];
            var content = (block["content"] as JsonArray ?? []).OfType<JsonNode>().ToList();
            var title = content is [var first, ..] && RichDocument.TypeOf(first) == "paragraph" ? first : null;
            var body = title is null ? content : content[1..];
            if (RequirementText.Title(block).Length == 0)
            {
                Cases.Add(new(RequirementCaseKind.EmptyTitle, oldId, ""));
            }
            var key = RequirementText.Value(block, "key");
            var attrs = new JsonObject { ["level"] = level, ["headingId"] = id };
            var heading = RichNodes.Node("heading", title?["content"]?.DeepClone() as JsonArray, attrs);
            var aliases = string.Join(',', new[] { oldId, key }.Where(x => x.Length > 0).Distinct(StringComparer.Ordinal));
            var children = body.Where(x => RichDocument.TypeOf(x) == "requirement").ToList();
            var dependencies = Dependencies(block);
            Before.Add(Facts(block, id, dependencies));
            if (children.Count > 0)
            {
                attrs["aliases"] = aliases;
                output.Add(heading);
                Cases.Add(new(RequirementCaseKind.Nested, oldId, string.Create(CultureInfo.InvariantCulture, $"{children.Count} nested")));
                if (NonDefault(block))
                {
                    output.Add(Paragraph(Text(RequirementHeadingReader.Former + RequirementText.Meta(block, names))));
                }
                var notes = body.Where(x => RichDocument.TypeOf(x) != "requirement").ToList();
                var moved = notes.Count(note => body.IndexOf(note) > body.IndexOf(children[0]));
                if (moved > 0)
                {
                    Cases.Add(new(RequirementCaseKind.NotesMoved, oldId, string.Create(CultureInfo.InvariantCulture, $"{moved} notes")));
                }
                output.AddRange(notes);
                return children.Select(child => Block((JsonObject)child, level + 1, output)).ToList()[^1];
            }
            attrs["state"] = RequirementText.Value(block, "state");
            attrs["stateLabel"] = RequirementText.Value(block, "stateLabel");
            attrs["direction"] = RequirementText.Value(block, "direction");
            attrs["system"] = RequirementText.Value(block, "system");
            attrs["from"] = From(key, false);
            attrs["aliases"] = aliases;
            var party = RequirementText.Value(block, "party");
            var contact = RequirementText.Value(block, "partyContactId");
            if (party.Length > 0)
            {
                attrs["party"] = party;
                if (contact.Length > 0)
                {
                    attrs["partyContactId"] = contact;
                }
            }
            output.Add(heading);
            var resolved = dependencies.Where(x => x.Target is not null).ToList();
            if (resolved.Count > 0)
            {
                var line = new JsonArray();
                foreach (var (_, target, targetTitle) in resolved)
                {
                    if (line.Count > 0)
                    {
                        line.Add(new JsonObject { ["type"] = "text", ["text"] = ", " });
                    }
                    var (page, heading2) = target!.Value;
                    line.Add(new JsonObject
                    {
                        ["type"] = "pageReference",
                        ["attrs"] = new JsonObject { ["id"] = page.ToString("D"), ["title"] = targetTitle.Length > 0 ? targetTitle : heading2, ["url"] = $"#/pages/{page:D}?heading={heading2}" },
                    });
                }
                output.Add(RichNodes.Node("paragraph", line, new JsonObject { ["dependencies"] = true }));
            }
            if (RequirementText.Value(block, "date") is { Length: > 0 } date)
            {
                output.Add(Paragraph(Text("do " + Day(date))));
            }
            foreach (var (reference, _, _) in dependencies.Where(x => x.Target is null))
            {
                output.Add(Paragraph(Text(RequirementHeadingReader.Missing + reference)));
                Cases.Add(new(RequirementCaseKind.UnresolvedKey, oldId, RequirementAttributes.CrossPage(reference) is null ? "same page" : "other page"));
            }
            if (RequirementText.Value(block, "where") is { Length: > 0 } where)
            {
                output.Add(Paragraph(Where(where)));
            }
            output.AddRange(body);
            return oldId;
        }

        /// <summary>The map's id for the block, unless a plain heading of the page already holds it: then the old id, else a new one.</summary>
        private string Assign(JsonObject block, string oldId)
        {
            var key = RequirementText.Value(block, "key");
            var id = map.HeadingId(pageId, oldId);
            var reason = id is null ? "not in map" : id == key ? null : key.Length == 0 ? "no key" : RequirementKeyMap.IsHeadingId(key) ? "key not unique" : "key outside charset";
            id ??= oldId;
            if (taken.Contains(id) || !RequirementKeyMap.IsHeadingId(id))
            {
                (id, reason) = !taken.Contains(oldId) && RequirementKeyMap.IsHeadingId(oldId) ? (oldId, "taken by a heading") : (Guid.NewGuid().ToString("D"), "new id");
            }
            if (reason is not null)
            {
                Cases.Add(new(RequirementCaseKind.IdFallback, oldId, reason));
            }
            taken.Add(id);
            return id;
        }

        /// <summary>Each <c>requires</c> entry with its target (page, heading id) and title, the target null when no block holds the key.</summary>
        private List<(string Reference, (Guid Page, string Heading)? Target, string Title)> Dependencies(JsonObject block)
        {
            var result = new List<(string, (Guid, string)?, string)>();
            foreach (var reference in RequirementAttributes.ParseRequires(RequirementText.Value(block, "requires")))
            {
                if (RequirementAttributes.CrossPage(reference) is { } cross && cross.PageId != pageId)
                {
                    result.Add(map.Resolve(cross.PageId, cross.Key) is { } other ? (reference, (cross.PageId, other.HeadingId), other.Title) : (reference, null, ""));
                    continue;
                }
                var key = RequirementAttributes.CrossPage(reference)?.Key ?? reference;
                if (ownKeys.TryGetValue(key, out var target))
                {
                    var targetId = RequirementText.Value(target, "id");
                    result.Add((reference, (pageId, ids[targetId]), RequirementText.Title(target)));
                }
                else
                {
                    result.Add((reference, null, ""));
                }
            }
            return result;
        }

        /// <summary>The catalog definition of <paramref name="key"/>: the first catalog page of the space (not this one) defining it. Expected
        /// (the report's before): a catalog page the map did not read reads as one that would define it, so a conversion with too small a map
        /// loses <c>from</c> and the gate refuses it instead of writing it empty.</summary>
        private string From(string key, bool expected)
        {
            foreach (var page in catalog.Where(page => page != pageId && key.Length > 0))
            {
                if (map.Resolve(page, key) is { } definition)
                {
                    return $"{page:D}#{definition.HeadingId}";
                }
                if (expected && !map.HasRead(page))
                {
                    return $"?{page:D}#{key}";
                }
            }
            return "";
        }

        public RequirementFacts Facts(JsonObject block, string id, List<(string Reference, (Guid Page, string Heading)? Target, string Title)> dependencies)
        {
            var content = (block["content"] as JsonArray ?? []).OfType<JsonNode>().ToList();
            var body = content is [var first, ..] && RichDocument.TypeOf(first) == "paragraph" ? content[1..] : content;
            var (notes, history) = RequirementHeadingReader.Body(body.Where(x => RichDocument.TypeOf(x) != "requirement"));
            var targets = dependencies.Select(x => x.Target is { } target ? $"{target.Page:D}#{target.Heading}" : "?" + x.Reference).Order(StringComparer.Ordinal).ToList();
            return new RequirementFacts(id, RequirementText.Value(block, "id"), RequirementText.Title(block), RequirementText.Value(block, "state"),
                RequirementText.Value(block, "stateLabel"), RequirementText.Value(block, "direction"), RequirementText.Value(block, "system"), targets,
                RequirementText.Value(block, "party"), RequirementText.Value(block, "partyContactId"), RequirementText.Value(block, "date"),
                RequirementText.Value(block, "where"), notes, history, From(RequirementText.Value(block, "key"), true),
                string.Join(',', new[] { RequirementText.Value(block, "id"), RequirementText.Value(block, "key") }.Where(x => x.Length > 0).Distinct(StringComparer.Ordinal)));
        }

        /// <summary>Whether a nested block's own fields say more than the defaults: they go to its „Dříve požadavek: …“ line.</summary>
        private static bool NonDefault(JsonObject block) => RequirementText.Value(block, "state") != "planned" || RequirementText.Value(block, "direction") != "internal"
            || new[] { "stateLabel", "party", "date", "system", "where", "requires" }.Any(name => RequirementText.Value(block, name).Length > 0);
    }

    /// <summary>Every old <c>requirement</c> block, nested ones included, in document order: the stored nodes only, not the requirement
    /// headings <see cref="RequirementText.Requirements"/> also reads as blocks.</summary>
    public static IEnumerable<JsonNode> Blocks(JsonNode node)
        => (node["content"] as JsonArray ?? []).OfType<JsonNode>()
            .SelectMany(child => RichDocument.TypeOf(child) == "requirement" ? Blocks(child).Prepend(child) : Blocks(child));

    /// <summary>One section of a converted document that does not read back from its Markdown: the place and type of its first
    /// differing block (never its text), and whether the conversion wrote the section (a requirement heading's, or a nested block's plain
    /// heading) or left it as it was.</summary>
    public sealed record RoundTripDifference(int Block, string Type, bool Converted);

    /// <summary>
    /// Why the converted envelope does not read back from its Markdown (the dialect's round trip, plan 2026-11-04 §2): the first section
    /// the conversion wrote that differs (<see cref="RoundTripDifferences"/>), or null when every one reads back.
    /// </summary>
    public static string? RoundTripFailure(string json)
        => RoundTripDifferences(json).FirstOrDefault(x => x.Converted) is { } difference
            ? string.Create(CultureInfo.InvariantCulture, $"block {difference.Block}: {difference.Type}") : null;

    /// <summary>
    /// The sections (a heading and the blocks up to the next heading, and the blocks before the first) that do not read back: each
    /// written alone by <see cref="EditorMarkdownWriter"/>, parsed by <see cref="MarkdownToRich"/> and compared block by block with
    /// <see cref="RichDocument.StructurallyEqual"/>, a requirement heading's id too. Left out, as Markdown never carried them, before this
    /// conversion as well: a heading's <c>aliases</c> (a section write keeps them), an empty paragraph, and the requirements overview,
    /// which the writer writes as its computed summary line. A section the conversion did not write is compared too, so the report can
    /// tell a difference the page already had from one the conversion made.
    /// </summary>
    public static IReadOnlyList<RoundTripDifference> RoundTripDifferences(string json)
    {
        var blocks = (RichDocument.Parse(json).Document["content"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var differences = new List<RoundTripDifference>();
        for (var start = 0; start < blocks.Count;)
        {
            var end = start + 1;
            while (end < blocks.Count && RichDocument.TypeOf(blocks[end]) != "heading")
            {
                end++;
            }
            var converted = RichDocument.TypeOf(blocks[start]) == "heading" && blocks[start]["attrs"]?["aliases"] is JsonValue aliases
                && aliases.TryGetValue<string>(out var list) && list.Length > 0;
            var stored = Enumerable.Range(start, end - start).Where(index => !RequirementText.IsSummary(blocks[index])
                && !(RichDocument.TypeOf(blocks[index]) == "paragraph" && blocks[index]["content"] is not JsonArray { Count: > 0 })).ToList();
            var written = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray([.. stored.Select(index => blocks[index].DeepClone())]) };
            var parsed = MarkdownToRich.Parse(EditorMarkdownWriter.Write(written), MarkdownToRich.PagePlugins).Blocks.OfType<JsonObject>().ToList();
            var read = AsMarkdownReads(stored.Select(index => (index, (JsonObject)blocks[index].DeepClone())));
            stored = [.. read.Select(x => x.Index)];
            for (var at = 0; at < Math.Max(stored.Count, parsed.Count); at++)
            {
                var block = at < stored.Count ? read[at].Block : null;
                if (block?["attrs"] is JsonObject attributes)
                {
                    attributes.Remove("aliases");
                }
                if (block is null || at >= parsed.Count || !RichDocument.StructurallyEqual(block, parsed[at])
                    || RequirementHeadingReader.IsRequirementHeading(block) && RequirementText.Value(block, "headingId") != RequirementText.Value(parsed[at], "headingId"))
                {
                    differences.Add(new(at < stored.Count ? stored[at] : end, block is null ? "-" : RichDocument.TypeOf(block), converted));
                    break;
                }
            }
            start = end;
        }
        return differences;
    }

    /// <summary>The blocks as Markdown reads them, which is no loss of this conversion (the page's Markdown read the same before it): a
    /// page reference is a link, and a bullet or timed list right after another is the same list (the kind of the first).</summary>
    private static List<(int Index, JsonObject Block)> AsMarkdownReads(IEnumerable<(int Index, JsonObject Block)> blocks)
    {
        var result = new List<(int Index, JsonObject Block)>();
        foreach (var (index, block) in blocks)
        {
            Links(block);
            if (result.Count > 0 && RichDocument.TypeOf(block) is "bulletList" or "timedList" && RichDocument.TypeOf(result[^1].Block) is "bulletList" or "timedList"
                && result[^1].Block["content"] is JsonArray items && block["content"] is JsonArray more)
            {
                var list = RichDocument.TypeOf(result[^1].Block);
                foreach (var item in more.ToList())
                {
                    more.Remove(item);
                    item!["type"] = list == "timedList" ? "timedItem" : "listItem";
                    items.Add(item);
                }
                continue;
            }
            result.Add((index, block));
        }
        return result;

        static void Links(JsonObject node)
        {
            // The dependency line's references are the dialect's own and read back as they are.
            if (node["content"] is not JsonArray content || RequirementHeadingMarkdown.IsDependencyLine(node))
            {
                return;
            }
            for (var i = 0; i < content.Count; i++)
            {
                if (content[i] is not JsonObject child)
                {
                    continue;
                }
                if (RichDocument.TypeOf(child) == "pageReference")
                {
                    content[i] = new JsonObject
                    {
                        ["type"] = "text", ["text"] = RequirementText.Value(child, "title").Trim(),
                        ["marks"] = new JsonArray(new JsonObject { ["type"] = "link", ["attrs"] = new JsonObject { ["href"] = RequirementText.Value(child, "url").Trim() } }),
                    };
                }
                else
                {
                    Links(child);
                }
            }
        }
    }

    private static JsonObject Paragraph(JsonArray content) => RichNodes.Node("paragraph", content);

    private static JsonArray Text(string text) => RichNodes.Text(text);

    /// <summary>„Kde uloženo: …“ with its web addresses as links; the rest (paths, „Atlas › stránka“, issue keys) stays text.</summary>
    private static JsonArray Where(string where)
    {
        var content = new JsonArray();
        var (text, at) = (RequirementHeadingReader.Stored, 0);
        foreach (Match url in Url().Matches(where))
        {
            text += where[at..url.Index];
            if (text.Length > 0)
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            }
            content.Add(new JsonObject { ["type"] = "text", ["text"] = url.Value, ["marks"] = new JsonArray(RichNodes.LinkMark(url.Value)) });
            (text, at) = ("", url.Index + url.Length);
        }
        text += where[at..];
        if (text.Length > 0)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }
        return content;
    }

    /// <summary>2026-11-05 → 5. 11. 2026; any other text stays as stored (the report then shows the deadline as lost).</summary>
    private static string Day(string date) => DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
        ? day.ToString("d. M. yyyy", CultureInfo.InvariantCulture) : date;

    /// <summary>A web address up to a space or „;“, without a trailing comma, period or bracket.</summary>
    [GeneratedRegex(@"https?://[^\s;]+(?<![,.)])")]
    private static partial Regex Url();
}
