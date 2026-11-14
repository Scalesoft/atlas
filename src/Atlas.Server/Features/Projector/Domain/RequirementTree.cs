using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.Projector.Domain;

/// <summary>
/// Requirement blocks in a page's rich content (<c>{format, version, document}</c>, spec §2): find, change, insert, move and remove
/// one, reset a copy, read the catalog definitions. Works on the parsed JSON in place; the caller saves it through the page save,
/// which validates the whole document again.
/// </summary>
public static class RequirementTree
{
    /// <summary>The category of a module with no heading above its own (plan 3.4 §5).</summary>
    public const string Core = "Jádro";

    /// <summary>Every requirement block of the document, nested ones included, in document order; a requirement heading as its block
    /// (<see cref="RequirementHeadings.View"/>, a copy: <see cref="Update"/> and the other writes change the heading).</summary>
    public static IEnumerable<JsonObject> Blocks(JsonObject rich, Guid? pageId = null) => RequirementText.Requirements(Document(rich), pageId).Cast<JsonObject>();

    /// <summary>The block of <paramref name="blockId"/>; a requirement heading's (its id, or a former one of its aliases) as its view.</summary>
    public static JsonObject Find(JsonObject rich, string blockId, Guid? pageId = null)
    {
        var page = Content(Document(rich));
        if (RequirementHeadingWrites.Find(page, blockId) is { } heading)
        {
            return RequirementHeadings.View(page, RequirementHeadings.RangeAt(page, heading), pageId, RequirementHeadings.LocalIds(page));
        }
        var (siblings, index) = Locate(rich, blockId);
        return (JsonObject)siblings[index]!;
    }

    /// <summary>A warning for each key an unfinished block requires that no block of the page has (a block of another page, `key@pageId`,
    /// and a block's own key aside), naming the blocks that need it: what a write leaves for the caller to add from the catalog.
    /// `only` limits the blocks looked at (the ones just written); none, every block of the page (plan 3.5 D).</summary>
    public static IReadOnlyList<string> MissingPrerequisites(JsonObject rich, IReadOnlySet<string>? only = null, Guid? pageId = null)
    {
        // No block written with requires (an ordinary update): nothing to look at, the page is not walked.
        if (only is { Count: 0 })
        {
            return [];
        }
        var blocks = Blocks(rich, pageId).ToArray();
        var have = blocks.Select(Key).ToHashSet(StringComparer.Ordinal);
        var missing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            if (only is not null && !only.Contains(RequirementAttributes.Text(block["attrs"], "id"))) continue;
            if (RequirementAttributes.Text(block["attrs"], "state") is "done" or "not_applicable") continue;
            foreach (var key in RequirementAttributes.ParseRequires(RequirementAttributes.Text(block["attrs"], "requires")))
            {
                if (key == Key(block) || RequirementAttributes.CrossPage(key) is not null || have.Contains(key)) continue;
                if (!missing.TryGetValue(key, out var needed)) missing[key] = needed = [];
                needed.Add($"„{Title(block)}“");
            }
        }
        return missing.Select(entry => $"Požadavek „{entry.Key}“ na stránce chybí; potřebuje ho {string.Join(", ", entry.Value)}. Vložte ho z katalogu (projector_insert_requirements).").ToArray();
    }

    /// <summary>The title as the index and the projection read it: the first paragraph's text, a line break as a space, trimmed.</summary>
    public static string Title(JsonObject block) => RequirementText.Title(block);

    public static string Key(JsonObject block) => RequirementAttributes.Text(block["attrs"], "key");

    /// <summary>Changes one block. A new key moves the <c>requires</c> of the page's other blocks with it; a cleared one takes the slug
    /// of the title (spec §2.1), and without a title the dependencies on the old key go. Returns what the change did to other blocks.</summary>
    public static IReadOnlyList<string> Update(JsonObject rich, string blockId, RequirementChange change, DateOnly today, Guid? pageId = null)
    {
        if (change.Requires?.Any(key => key is null) == true)
        {
            throw RequirementAttributes.InvalidArgument("requires", "requires: závislosti jsou klíče; prázdnou položku vynechte.");
        }
        if (RequirementHeadingWrites.Find(Content(Document(rich)), blockId) is { } heading)
        {
            return RequirementHeadingWrites.Update(rich, heading, change, today, pageId ?? throw new InvalidOperationException("A heading's write needs its page."));
        }
        var block = Find(rich, blockId);
        var attrs = Attrs(block);
        var oldKey = Key(block);
        if (change.Title is { } title)
        {
            var content = Content(block);
            if (content.Count == 0)
            {
                content.Add(Paragraph(title));
            }
            else
            {
                content[0] = Paragraph(title);
            }
        }
        if (change.NotesMarkdown is { } notes)
        {
            RequirementText.ReplaceNotes(rich, block, notes);
        }
        // After the notes: they bring their nested blocks back as new nodes, whose requires a new key moves too (S2-1).
        var others = Blocks(rich).Where(other => other != block).ToArray();
        var otherKeys = others.Select(Key).Where(key => key.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (change.Key is { } key)
        {
            attrs["key"] = key.Trim().Length > 0 ? key.Trim() : RequirementAttributes.Unique(RequirementAttributes.Slug(Title(block)), otherKeys);
        }
        Apply(attrs, change, today);
        RequirementAttributes.Validate(attrs, otherKeys);
        return oldKey.Length > 0 && Key(block) != oldKey ? MoveDependencies(others, oldKey, Key(block)) : [];
    }

    /// <summary>The attributes of <paramref name="change"/> other than the key, title and notes, on a block's or a heading view's attrs.</summary>
    internal static void Apply(JsonObject attrs, RequirementChange change, DateOnly today)
    {
        Set(attrs, "direction", change.Direction);
        // The block's own word was for the state it had (plan 3.4 §4), unless the change gives one.
        if (change.State is { } state && state.Trim() != RequirementAttributes.Text(attrs, "state") && change.StateLabel is null)
        {
            attrs["stateLabel"] = "";
        }
        Set(attrs, "state", change.State);
        Set(attrs, "stateLabel", change.StateLabel);
        Set(attrs, "system", change.System);
        Set(attrs, "date", change.Date);
        Set(attrs, "party", change.Party);
        if (change.PartyContactId is { } contact)
        {
            attrs["partyContactId"] = contact.Trim();
        }
        else if (change.Party is not null && RequirementAttributes.Text(attrs, "partyContactId").Length > 0)
        {
            // A party written as text is no longer the contact it named.
            attrs["partyContactId"] = "";
        }
        Set(attrs, "where", change.Where);
        Set(attrs, "requires", change.Requires is { } requires ? string.Join(",", requires.Select(x => x.Trim())) : null);
        if (attrs.ContainsKey("completed"))
        {
            // A heading's view: done is dated today on its facts line beside the deadline („hotovo“), any other state takes the day off.
            if (change.State is { } now)
            {
                attrs["completed"] = now.Trim() != "done" ? "" : RequirementAttributes.Text(attrs, "completed") is { Length: > 0 } was ? was : today.ToString("yyyy-MM-dd");
            }
        }
        else if (change.State == "done" && RequirementAttributes.Text(attrs, "date").Length == 0)
        {
            // Spec §2.1: done without a date is dated today.
            attrs["date"] = today.ToString("yyyy-MM-dd");
        }
    }

    /// <summary>What the server writes (spec §2.1): a block without an id or with one an earlier block has gets a new one, so every
    /// block is addressable; a block without a key gets the slug of its title, unique on the page.</summary>
    public static void Normalize(JsonObject rich)
    {
        RequirementHeadingWrites.Normalize(Content(Document(rich)));
        var blocks = Blocks(rich).Where(block => !RequirementHeadings.IsView(block)).ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var keys = blocks.Select(Key).Where(key => key.Length > 0).ToHashSet(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            var attrs = Attrs(block);
            if (RequirementAttributes.Text(attrs, "id") is not { Length: > 0 } id || !ids.Add(id))
            {
                attrs["id"] = Guid.NewGuid().ToString("D");
            }
            if (Key(block).Length == 0 && RequirementAttributes.Unique(RequirementAttributes.Slug(Title(block)), keys) is { Length: > 0 } slug)
            {
                attrs["key"] = slug;
                keys.Add(slug);
            }
        }
    }

    /// <summary>Copies of <paramref name="blocks"/> placed at <paramref name="position"/>: new ids, the state reset as in a copy, and a
    /// key the page already has made unique with a suffix (<c>-2</c>, spec §2.1). Returns the inserted copies.
    /// On a page of requirement headings (<see cref="RequirementHeadingWrites.UsesHeadings"/>) each copy becomes a heading's section there,
    /// one level under the plain heading it goes under (its blockId then its headingId).</summary>
    public static IReadOnlyList<JsonObject> Insert(JsonObject rich, IReadOnlyList<JsonObject> blocks, RequirementPosition position, Guid? pageId = null)
    {
        var copies = Copy(rich, blocks);
        if (RequirementHeadingWrites.UsesHeadings(rich))
        {
            RequirementHeadingWrites.Place(rich, copies, position, PageOf(pageId));
        }
        else
        {
            Place(rich, AsBlocks(copies), position);
        }
        // The document names the plugin of every node it now holds, as the editor does when it inserts one.
        RichMarkdownMerge.DeclarePlugins(rich, Document(rich));
        return copies;
    }

    /// <summary>Copies of <paramref name="blocks"/> as <see cref="Insert"/> makes them, each placed at the end of the page section its
    /// path names (<c>[product, category, module]</c>, the editor's placeOnPage: top-level headings only, text compared without case and
    /// diacritics) with the headings the path lacks created there, one level deeper each; one that matches no heading goes to the end of
    /// the page. Returns the copies in the order of <paramref name="blocks"/>.</summary>
    public static IReadOnlyList<JsonObject> InsertByPath(JsonObject rich, IReadOnlyList<JsonObject> blocks, IReadOnlyList<IReadOnlyList<string>> paths,
        Guid? pageId = null)
    {
        var copies = Copy(rich, blocks);
        var page = Content(Document(rich));
        var headings = RequirementHeadingWrites.UsesHeadings(rich);
        if (!headings)
        {
            AsBlocks(copies);
        }
        for (var i = 0; i < copies.Count; i++)
        {
            if (Section(page, paths[i]) is not { } section)
            {
                Put(page, page.Count, copies[i], RequirementHeadingWrites.Target(page, new RequirementPosition()).Level);
                continue;
            }
            var (index, level, found) = section;
            foreach (var name in paths[i].Skip(found))
            {
                page.Insert(index++, Heading(name, level));
                level = Math.Min(6, level + 1);
            }
            Put(page, index, copies[i], level);
        }
        void Put(JsonArray content, int index, JsonObject copy, int level)
        {
            JsonNode[] nodes = headings ? [.. RequirementHeadingWrites.Section(content, copy, level, PageOf(pageId))] : [copy];
            for (var n = 0; n < nodes.Length; n++)
            {
                content.Insert(index + n, nodes[n]);
            }
        }
        RichMarkdownMerge.DeclarePlugins(rich, Document(rich));
        return copies;
    }

    /// <summary>Whether <see cref="InsertByPath"/> would place a block of <paramref name="path"/> under a heading of the page.</summary>
    public static bool OnPage(JsonObject rich, IReadOnlyList<string> path) => Section(Content(Document(rich)), path) is not null;

    /// <summary>The end of the deepest section of the page the path names (the index of the node after it), the level a heading created
    /// there takes and how many names of the path were found; null when not even the first is.</summary>
    private static (int Index, int Level, int Found)? Section(JsonArray page, IReadOnlyList<string> path)
    {
        var headings = page.Select((node, index) => (Node: node, Index: index)).Where(x => Type(x.Node) == "heading")
            .Select(x => (Text: PlainText(x.Node).Trim(), Level: Level(x.Node), x.Index, Requirement: RequirementHeadings.IsRequirement(x.Node))).ToList();
        var czech = CultureInfo.GetCultureInfo("cs-CZ");
        int end = page.Count, from = 0, level = 0, found = 0;
        foreach (var name in path)
        {
            // A section is a plain heading's; a requirement heading only ends one.
            var at = headings.FindIndex(h => h.Index >= from && h.Index < end && h.Level > level && !h.Requirement
                && string.Compare(h.Text, name.Trim(), czech, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0);
            if (at < 0)
            {
                break;
            }
            var heading = headings[at];
            if (headings.FindIndex(next => next.Index > heading.Index && next.Level <= heading.Level) is >= 0 and var closing)
            {
                end = Math.Min(end, headings[closing].Index);
            }
            (from, level) = (heading.Index, heading.Level);
            found++;
        }
        return found > 0 ? (end, Math.Min(6, level + 1), found) : null;
    }

    private static List<JsonObject> Copy(JsonObject rich, IReadOnlyList<JsonObject> blocks)
    {
        var taken = Blocks(rich).Select(Key).Where(key => key.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (RequirementHeadingWrites.UsesHeadings(rich))
        {
            // A key becomes a headingId, unique among every heading of the page.
            taken.UnionWith(Content(Document(rich)).Where(RequirementHeadings.IsHeading).Select(RequirementHeadings.HeadingId).Where(id => id.Length > 0));
        }
        var copies = blocks.Select(block => (JsonObject)block.DeepClone()).ToList();
        foreach (var block in copies.SelectMany(copy => RequirementText.Requirements(copy)).Cast<JsonObject>())
        {
            Reset(block);
            if (RequirementAttributes.Unique(Key(block), taken) is { Length: > 0 } key)
            {
                Attrs(block)["key"] = key;
                taken.Add(key);
            }
        }
        return copies;
    }

    /// <summary>Copies to be stored as requirement blocks: without what only a heading's view has (its level, <c>from</c>).</summary>
    private static List<JsonObject> AsBlocks(List<JsonObject> copies)
    {
        foreach (var block in copies.SelectMany(copy => RequirementText.Requirements(copy)).Cast<JsonObject>())
        {
            Attrs(block).Remove("level");
            Attrs(block).Remove("from");
        }
        return copies;
    }

    private static Guid PageOf(Guid? pageId) => pageId ?? throw new InvalidOperationException("A requirement heading's write needs its page id.");

    /// <summary>A heading of <paramref name="text"/>, as a whole module of the catalog is inserted under it.</summary>
    public static JsonObject Heading(string text, int level)
    {
        var heading = Paragraph(text);
        heading["type"] = "heading";
        heading["attrs"] = new JsonObject { ["level"] = level };
        return heading;
    }

    /// <summary>The level a heading inserted at <paramref name="position"/> takes: one below the heading it goes under (a module under
    /// its product, plan 3.4 §6): the position's heading, else the page's heading above that place; 2 where there is none or inside a
    /// block, at most 6.</summary>
    public static int HeadingLevel(JsonObject rich, RequirementPosition position)
    {
        if (RequirementHeadingWrites.UsesHeadings(rich))
        {
            return RequirementHeadingWrites.Target(Content(Document(rich)), position).Level;
        }
        var (target, index) = Target(rich, position);
        var page = Content(Document(rich));
        var above = position.HeadingId is { } headingId ? page.First(node => Type(node) == "heading" && RequirementAttributes.Text(node!["attrs"], "headingId") == headingId)
            : target == page ? target.Take(index).LastOrDefault(node => Type(node) == "heading") : null;
        return Math.Min(6, (above is null ? 1 : Level(above)) + 1);
    }

    public static void Move(JsonObject rich, string blockId, RequirementPosition position)
    {
        if (RequirementHeadingWrites.Find(Content(Document(rich)), blockId) is { } heading)
        {
            RequirementHeadingWrites.Move(Content(Document(rich)), heading, position);
            return;
        }
        var (siblings, index) = Locate(rich, blockId);
        var block = (JsonObject)siblings[index]!;
        if ((position.AfterBlockId ?? position.IntoBlockId) is { } target && (target == blockId || TryLocate(block, target) is not null))
        {
            throw InvalidPosition("Požadavek nejde přesunout do sebe sama.");
        }
        Detach(siblings, index);
        Place(rich, [block], position);
    }

    /// <summary>Takes the block out with its content; a requirement heading with its section.</summary>
    public static void Remove(JsonObject rich, string blockId)
    {
        var page = Content(Document(rich));
        if (RequirementHeadingWrites.Find(page, blockId) is { } heading)
        {
            RequirementHeadingWrites.Detach(page, heading);
            if (page.Count == 0)
            {
                page.Add(Paragraph(""));
            }
            return;
        }
        var (siblings, index) = Locate(rich, blockId);
        Detach(siblings, index);
    }

    /// <summary>A new deployment from this page (spec §1.5): every requirement gets a new id, state planned and an empty date, party
    /// and where; key, direction, requires and content stay. A requirement heading keeps its id (its key), loses its aliases, its own
    /// state word, its facts line and its „Kde uloženo“ paragraph.</summary>
    public static void ResetForCopy(JsonObject rich)
    {
        foreach (var block in Blocks(rich).Where(block => !RequirementHeadings.IsView(block)))
        {
            Reset(block);
        }
        RequirementHeadingWrites.ResetForCopy(Content(Document(rich)));
    }

    /// <summary>O8 (owner 2. 11. 2026): a new zakázka copied from this page takes its structure, not the job's values. Each table outside the
    /// requirement blocks keeps its header cells and first column (the row labels), its other cells lose their content (a requirement block
    /// in one stays); the top-level section „Kontakty“ keeps only its heading. The blocks themselves are <see cref="ResetForCopy"/>'s.</summary>
    public static void EmptyForCopy(JsonObject rich)
    {
        var page = Content(Document(rich));
        for (var i = 0; i < page.Count; i++)
        {
            if (Type(page[i]) == "heading" && string.Equals(PlainText(page[i]).Trim(), "Kontakty", StringComparison.OrdinalIgnoreCase))
            {
                var level = Level(page[i]);
                while (i + 1 < page.Count && !(Type(page[i + 1]) == "heading" && Level(page[i + 1]) <= level))
                {
                    page.RemoveAt(i + 1);
                }
            }
        }
        // A requirement heading's section is the requirement's, as a block's content.
        EmptyTables(page, RequirementHeadings.Ranges(page).SelectMany(range => Enumerable.Range(range.Heading, range.End - range.Heading))
            .Select(index => page[index]).OfType<JsonNode>().ToHashSet<JsonNode>(ReferenceEqualityComparer.Instance));
    }

    private static void EmptyTables(JsonArray content, IReadOnlySet<JsonNode>? kept = null)
    {
        foreach (var node in content.OfType<JsonObject>().Where(node => !IsRequirement(node) && kept?.Contains(node) != true))
        {
            if (Type(node) != "table")
            {
                if (node["content"] is JsonArray inner)
                {
                    EmptyTables(inner);
                }
                continue;
            }
            foreach (var cell in Content(node).OfType<JsonObject>().SelectMany(row => Content(row).OfType<JsonObject>().Skip(1)).Where(cell => Type(cell) == "tableCell"))
            {
                var items = Content(cell);
                foreach (var value in items.Where(item => item is not JsonObject child || !IsRequirement(child)).ToList())
                {
                    items.Remove(value);
                }
                if (items.Count == 0)
                {
                    items.Add(Paragraph(""));
                }
            }
        }
    }

    /// <summary>The catalog of one page (spec §1.4): its top-level blocks with a key, under the heading above them (its text and its
    /// module key: the headingId, else the slug of the text), the heading that one is under (its category, <see cref="Core"/> without
    /// one) and the heading above that (its product, "" without one; a heading closes the ones of its level and deeper, as
    /// RequirementText.Sectioned) and the plain text of the paragraphs under the heading before its first requirement block (its
    /// summary, joined by a space, "" before the first heading), in document order; a repeated key is left to
    /// <see cref="RequirementResolver.Distinct"/>.</summary>
    // shortcut: nested blocks are content of their top-level definition, not definitions of their own; split them out when a catalog
    // needs to offer a nested block alone.
    public static IReadOnlyList<CatalogDefinition> Definitions(Guid pageId, JsonObject rich)
    {
        var bundle = "";
        var module = "";
        var category = Core;
        var product = "";
        var summary = "";
        var describing = false;
        var open = new List<(string Text, int Level)>();
        var definitions = new List<CatalogDefinition>();
        // A requirement heading is a definition (its view, from = its own place), not a module.
        foreach (var node in RequirementHeadings.Project(Document(rich), pageId).OfType<JsonObject>())
        {
            if (RequirementHeadings.IsView(node) && RequirementAttributes.Text(node["attrs"], "from").Length == 0 && Key(node).Length > 0)
            {
                Attrs(node)["from"] = RequirementHeadings.Reference(pageId, Key(node));
            }
            // The module's own paragraphs: those between its heading and its first requirement block.
            if (describing && Type(node) == "paragraph" && PlainText(node).Trim() is { Length: > 0 } text)
            {
                summary = summary.Length > 0 ? summary + " " + text : text;
            }
            if (IsRequirement(node))
            {
                describing = false;
            }
            if (Type(node) == "heading")
            {
                summary = "";
                describing = true;
                bundle = PlainText(node);
                module = RequirementAttributes.Text(node["attrs"], "headingId") is { Length: > 0 } headingId ? headingId : RequirementAttributes.Slug(bundle);
                open.RemoveAll(entry => entry.Level >= Level(node));
                category = open.Count > 0 && open[^1].Text.Trim() is { Length: > 0 } above ? above : Core;
                product = open.Count > 1 ? open[^2].Text.Trim() : "";
                open.Add((bundle, Level(node)));
            }
            else if (IsRequirement(node) && Key(node) is { Length: > 0 } key)
            {
                var title = Content(node).FirstOrDefault()?.DeepClone() ?? Paragraph("");
                var markdown = EditorMarkdownWriter.Write(new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(title) });
                definitions.Add(new CatalogDefinition(key, Title(node), markdown.Trim(), RequirementAttributes.Text(node["attrs"], "direction"),
                    RequirementAttributes.ParseRequires(RequirementAttributes.Text(node["attrs"], "requires")), (JsonObject)node.DeepClone(), pageId, bundle, module, category, product, summary));
            }
        }
        return definitions;
    }

    /// <summary>The text of the document's first top-level paragraph that is not empty, "" when there is none.</summary>
    public static string FirstParagraph(JsonObject rich)
        => Content(Document(rich)).OfType<JsonObject>().Where(node => Type(node) == "paragraph").Select(node => PlainText(node).Trim()).FirstOrDefault(text => text.Length > 0) ?? "";

    /// <summary>The text of a requirement block's content as Markdown, to compare two definitions of one key.</summary>
    public static string ContentMarkdown(JsonObject block)
        => EditorMarkdownWriter.Write(new JsonObject { ["type"] = "doc", ["content"] = Content(block).DeepClone() }).Trim();

    /// <summary>The <c>requires</c> of <paramref name="others"/> follow a key from <paramref name="oldKey"/> to <paramref name="newKey"/>,
    /// or drop it when the block has no key any more.</summary>
    private static IReadOnlyList<string> MoveDependencies(IReadOnlyList<JsonObject> others, string oldKey, string newKey)
    {
        var dependents = others.Where(other => RequirementAttributes.ParseRequires(RequirementAttributes.Text(other["attrs"], "requires")).Contains(oldKey)).ToArray();
        foreach (var dependent in dependents)
        {
            var requires = RequirementAttributes.ParseRequires(RequirementAttributes.Text(dependent["attrs"], "requires"))
                .Select(key => key == oldKey ? newKey : key).Where(key => key.Length > 0 && key != Key(dependent)).Distinct(StringComparer.Ordinal);
            Attrs(dependent)["requires"] = string.Join(",", requires);
        }
        if (dependents.Length == 0)
        {
            return [];
        }
        var names = string.Join(", ", dependents.Select(x => $"„{Title(x)}“"));
        return [newKey.Length > 0
            ? $"Závislosti požadavků {names} teď vedou na nový klíč „{newKey}“."
            : $"Požadavky {names} už nezávisí na „{oldKey}“: požadavek bez názvu nemá klíč."];
    }

    /// <summary>Takes the node out of its container. A container needs a block (the page, a callout, a quote, a table cell), so an
    /// emptied one keeps an empty paragraph; a requirement keeps its title paragraph and never runs empty.</summary>
    private static void Detach(JsonArray siblings, int index)
    {
        siblings.RemoveAt(index);
        if (siblings.Count == 0)
        {
            siblings.Add(Paragraph(""));
        }
    }

    internal static void Reset(JsonObject block)
    {
        StripRecords(block);
        var attrs = Attrs(block);
        attrs["id"] = Guid.NewGuid().ToString("D");
        attrs["state"] = "planned";
        attrs.Remove("stateLabel");
        attrs["date"] = "";
        attrs["party"] = "";
        attrs.Remove("partyContactId");
        attrs["where"] = "";
    }

    // „15. 10. 2026 14:05 — text“ (requirementHistory.ts historyStamp): a history line of the notes, a record of the job, not the requirement's text;
    // a timed item's fresh stamp without text too (timedList.ts).
    // The hours are limited here: .NET 11's TimeOnly takes „24:00“ (the end of the day), the editor does not.
    private static readonly Regex HistoryLine = new(@"^(?<date>\d{1,2}\.\s*\d{1,2}\.\s*\d{4})(?:\s+(?<time>(?:[01]?\d|2[0-3]):[0-5]\d))?\s+[—–-](?:\s+(?<text>\S.*))?$", RegexOptions.CultureInvariant);

    /// <summary>A history line's stamp on a day in the calendar (and a time of the day), else null: „31. 2. 2026 —“ is text, as in the
    /// editor (tests/fixtures/content/history-stamps.json).</summary>
    private static Match? HistoryStamp(string text)
    {
        var line = HistoryLine.Match(text);
        var date = Regex.Replace(line.Groups["date"].Value, @"\s", "");
        return line.Success && DateOnly.TryParseExact(date, "d.M.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && (!line.Groups["time"].Success || TimeOnly.TryParseExact(line.Groups["time"].Value, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            ? line : null;
    }

    /// <summary>Whether a list line's text (without its marker) is a record of the requirement's history.</summary>
    public static bool IsHistoryLine(string text) => HistoryStamp(text.Trim()) is not null;

    /// <summary>The history records of a block's own notes in the order written (requirementHistory.ts historyEntries): the list lines
    /// that start with a date, day as yyyy-MM-dd and time as HH:mm (Europe/Prague), whole; a fresh stamp with no text yet is none, and a
    /// nested block's lines are its own.</summary>
    public static IReadOnlyList<(string Date, string? Time, string Text)> History(JsonObject block)
    {
        var entries = new List<(string, string?, string)>();
        void Walk(IEnumerable<JsonNode?> nodes)
        {
            foreach (var element in nodes.OfType<JsonObject>().Where(x => !IsRequirement(x)))
            {
                var content = element["content"] as JsonArray ?? [];
                if (Type(element) is "listItem" or "timedItem" && HistoryStamp(PlainText(content.FirstOrDefault()).Trim()) is { } line
                    && line.Groups["text"].Value.Trim() is { Length: > 0 } text)
                {
                    var day = DateOnly.ParseExact(Regex.Replace(line.Groups["date"].Value, @"\s", ""), "d.M.yyyy", CultureInfo.InvariantCulture);
                    var time = line.Groups["time"].Success ? TimeOnly.ParseExact(line.Groups["time"].Value, "H:mm", CultureInfo.InvariantCulture) : (TimeOnly?)null;
                    entries.Add((day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), time?.ToString("HH:mm", CultureInfo.InvariantCulture), text));
                }
                Walk(content);
            }
        }
        // The title paragraph is no record.
        Walk((block["content"] as JsonArray ?? []).Skip(1));
        return entries;
    }

    /// <summary>Every requirement block of the document as the index reads it (atlas_projector_requirements): in document order, numbered
    /// from 1, each with the block it is nested in; a requirement heading as its view, with its dependencies relative to <paramref name="pageId"/>.</summary>
    public static IReadOnlyList<Requirement> Read(JsonObject rich, Guid? pageId = null)
    {
        var blocks = new List<Requirement>();
        void Walk(IEnumerable<JsonNode?> nodes, string? parent)
        {
            foreach (var child in nodes)
            {
                if (child is not JsonObject element)
                {
                    continue;
                }
                if (!IsRequirement(element))
                {
                    Walk(element["content"] as JsonArray ?? [], parent);
                    continue;
                }
                var attrs = element["attrs"];
                var id = RequirementAttributes.Text(attrs, "id");
                blocks.Add(new Requirement(id, Key(element), Title(element), RequirementAttributes.Text(attrs, "direction"),
                    RequirementAttributes.Text(attrs, "state"), RequirementAttributes.Text(attrs, "date"), RequirementAttributes.Text(attrs, "party"),
                    RequirementAttributes.Text(attrs, "where"), RequirementAttributes.ParseRequires(RequirementAttributes.Text(attrs, "requires")),
                    parent, blocks.Count + 1)
                {
                    PartyContactId = RequirementAttributes.Text(attrs, "partyContactId"),
                    Completed = RequirementAttributes.Text(attrs, "completed") is { Length: > 0 } completed ? completed : null
                });
                Walk(element["content"] as JsonArray ?? [], id);
            }
        }
        Walk(RequirementHeadings.Project(Document(rich), pageId), null);
        return blocks;
    }
    // The line „Blokuje…“ writes (requirementBlockView.ts record): its reason is also the notes' first paragraph.
    private static readonly Regex BlockedLine = new(@"^Blokováno:\s+(?<reason>.+?)(?:\s+\(čekali jsme na .*\))?$", RegexOptions.CultureInvariant);

    /// <summary>A copy takes the requirement, not the job's records (plan 3.5 C, as the Standardizace preset of 3.4: the structure without
    /// the job's data): the notes' history lines (list items starting with a date; a list they empty goes too) and the reason „Blokuje…“
    /// put under the title, the paragraph a „Blokováno: …“ line names. The rest of the notes stays; a nested block is reset on its own.</summary>
    internal static void StripRecords(JsonObject block)
    {
        var content = Content(block);
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in content.OfType<JsonObject>().Where(node => Type(node) is "bulletList" or "orderedList" or "timedList").ToList())
        {
            var items = Content(list);
            foreach (var item in items.OfType<JsonObject>().ToList())
            {
                if (HistoryStamp(PlainText(Content(item).FirstOrDefault()).Trim()) is not { } line)
                {
                    continue;
                }
                if (BlockedLine.Match(line.Groups["text"].Value.Trim()) is { Success: true } blocked)
                {
                    reasons.Add(blocked.Groups["reason"].Value.Trim());
                }
                items.Remove(item);
            }
            if (items.Count == 0)
            {
                content.Remove(list);
            }
        }
        if (content.Count > 1 && Type(content[1]) == "paragraph" && reasons.Contains(PlainText(content[1]).Trim()))
        {
            content.RemoveAt(1);
        }
    }

    private static void Place(JsonObject rich, IReadOnlyList<JsonObject> nodes, RequirementPosition position)
    {
        var (target, index) = Target(rich, position);
        for (var i = 0; i < nodes.Count; i++)
        {
            target.Insert(index + i, nodes[i]);
        }
    }

    private static (JsonArray Target, int Index) Target(JsonObject rich, RequirementPosition position)
    {
        if (new[] { position.AfterBlockId, position.IntoBlockId, position.HeadingId }.Count(x => x is not null) > 1)
        {
            throw InvalidPosition("Zadejte nejvýše jedno místo: za požadavek, do požadavku, nebo do oddílu.");
        }
        if (position.AfterBlockId is { } after)
        {
            var (siblings, index) = Locate(rich, after);
            return (siblings, index + 1);
        }
        if (position.IntoBlockId is { } into)
        {
            var content = Content(Find(rich, into));
            return (content, content.Count);
        }
        var page = Content(Document(rich));
        if (position.HeadingId is not { } headingId)
        {
            return (page, page.Count);
        }
        // The heading's section ends before the next heading of the same or a higher level.
        var heading = page.Select((node, index) => (Node: node, Index: index))
            .FirstOrDefault(x => Type(x.Node) == "heading" && RequirementAttributes.Text(x.Node!["attrs"], "headingId") == headingId);
        if (heading.Node is null)
        {
            throw new ApiException(404, "Nadpis na stránce není.", "heading_not_found");
        }
        var level = Level(heading.Node);
        var end = page.Select((node, index) => (Node: node, Index: index))
            .FirstOrDefault(x => x.Index > heading.Index && Type(x.Node) == "heading" && Level(x.Node) <= level, (null, page.Count));
        return (page, end.Index);
    }

    private static (JsonArray Siblings, int Index) Locate(JsonObject rich, string blockId)
    {
        return TryLocate(Document(rich), blockId) ?? throw new ApiException(404, "Požadavek na stránce není.", "requirement_not_found");
    }

    private static (JsonArray Siblings, int Index)? TryLocate(JsonObject node, string blockId)
    {
        var content = node["content"] as JsonArray;
        for (var i = 0; i < (content?.Count ?? 0); i++)
        {
            if (content![i] is not JsonObject child)
            {
                continue;
            }
            if (IsRequirement(child) && RequirementAttributes.Text(child["attrs"], "id") == blockId)
            {
                return (content, i);
            }
            if (TryLocate(child, blockId) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static string PlainText(JsonNode? node)
    {
        if (node is not JsonObject element)
        {
            return "";
        }
        return Type(element) == "text"
            ? RequirementAttributes.Text(element, "text")
            : string.Concat((element["content"] as JsonArray ?? []).Select(PlainText));
    }

    internal static JsonObject Paragraph(string text)
    {
        return text.Length == 0
            ? new JsonObject { ["type"] = "paragraph" }
            : new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
    }

    private static JsonObject Document(JsonObject rich)
    {
        return rich["document"] as JsonObject ?? throw new ApiException(400, "Obsah stránky nemá dokument.", "invalid_document");
    }

    private static JsonArray Content(JsonObject node)
    {
        if (node["content"] is not JsonArray content)
        {
            node["content"] = content = [];
        }
        return content;
    }

    private static JsonObject Attrs(JsonObject block)
    {
        if (block["attrs"] is not JsonObject attrs)
        {
            block["attrs"] = attrs = new JsonObject();
        }
        return attrs;
    }

    private static void Set(JsonObject attrs, string name, string? value)
    {
        if (value is not null)
        {
            attrs[name] = value.Trim();
        }
    }

    private static int Level(JsonNode? heading) => heading?["attrs"]?["level"] is JsonValue value && value.TryGetValue<int>(out var level) ? level : 1;

    private static string Type(JsonNode? node) => RequirementAttributes.Text(node, "type");

    private static bool IsRequirement(JsonObject node) => Type(node) == RequirementAttributes.NodeType;

    internal static ApiException InvalidPosition(string message) => new(400, message, "invalid_position");
}
