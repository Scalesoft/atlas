using System.Globalization;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// The readable text of Projector's requirement blocks (spec 2026-10-27 §2.3), as the web writes it (src/Atlas.Web/src/projector/requirementSummary.ts;
/// tests/fixtures/content/requirements.json keeps the two equal). Attributes a stored node leaves out read as the schema's defaults.
/// </summary>
internal static class RequirementText
{
    public const string SummaryLabel = "Stav nasazení";

    private static readonly Dictionary<string, string> States = new(StringComparer.Ordinal)
    {
        ["planned"] = "neřešeno", ["waiting_for_input"] = "čeká na vstup", ["in_progress"] = "rozpracováno", ["done"] = "hotovo",
        ["blocked"] = "blokováno", ["not_applicable"] = "netýká se",
    };

    /// <summary>The web's <c>directionWords</c>: the word of the badge and the projection, the party word.</summary>
    private static readonly Dictionary<string, (string Label, string Party)> Directions = new(StringComparer.Ordinal)
    {
        ["internal"] = ("naše práce", "kdo"), ["inbound"] = ("vstup", "od"), ["outbound"] = ("předání", "komu"),
    };

    /// <summary>The schema's defaults of the attributes a stored node may leave out (spec §2.1); any other reads as empty.</summary>
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal) { ["direction"] = "internal", ["state"] = "planned" };

    /// <summary>A string property of <paramref name="attrs"/> (a node's attrs, or any JSON object), the schema's default when left out:
    /// the one reader of requirement attributes, here and in Projector.</summary>
    public static string Attr(JsonNode? attrs, string name)
        => attrs?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : Defaults.GetValueOrDefault(name, "");

    public static string Value(JsonNode? node, string name) => Attr(node?["attrs"], name);

    /// <summary>Free text written into Markdown: a <c>&lt;</c> must not read as HTML, the same as the editor writes text nodes.</summary>
    public static string Html(string text) => text.Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    public static bool IsDone(JsonNode node) => Value(node, "state") == "done";

    /// <summary>The page overview of kind <c>requirements</c> (src/Atlas.Web/src/overview), or Atlas 3.3's summary block it was before
    /// (LegacyDocumentRepair renames it; a stored copy read without the repair still writes the same line).</summary>
    public static bool IsSummary(JsonNode? node) => node?["type"] is JsonValue type && type.TryGetValue<string>(out var name)
        && (name == "requirementSummary" || (name == "overview" && Value(node, "kind") == "requirements"));

    /// <summary>The task-list marker: [x] done, [-] does not apply, [ ] anything else; the state word follows in the line.</summary>
    public static string Checkbox(JsonNode node) => Value(node, "state") switch
    {
        "done" => "- [x] ",
        "not_applicable" => "- [-] ",
        _ => "- [ ] ",
    };

    /// <summary>
    /// How the document's dependencies read, by key: the title of the block, "title (key)" when two blocks share the title, the key when
    /// the block has none. A key without a block in the document is not in the map and reads as itself.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DependencyNames(JsonNode document)
    {
        var blocks = Requirements(document).Select(x => (Key: Value(x, "key"), Title: Title(x))).ToList();
        var shared = blocks.Where(x => x.Title.Length > 0).GroupBy(x => x.Title, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, title) in blocks.Where(x => x.Key.Length > 0))
        {
            // A repeated key (the editor makes keys unique) is named by its last block, as the web's Map keeps the last entry.
            names[key] = title.Length == 0 ? key : shared[title] > 1 ? title + " (" + key + ")" : title;
        }
        return names;
    }

    /// <summary>The text of the first paragraph, a line break read as a space, a YouTrack issue as its key (as its Markdown starts); other
    /// inline components are left out, as on the web.</summary>
    public static string Title(JsonNode node)
    {
        return string.Concat((node["content"]?[0]?["content"] as JsonArray ?? []).Select(x => x?["type"]?.GetValue<string>() switch
        {
            "text" => x!["text"]?.GetValue<string>() ?? "",
            "hardBreak" => " ",
            "youtrackIssue" => Attr(x!["attrs"], "key").Trim(),
            _ => "",
        })).Trim(' ');
    }

    /// <summary>"hotovo 10. 9. 2026 · vstup · od: IT KBN · služba: NIA · uloženo: Keepass · klíč: portal.ssl · závisí na: SSL certifikát, b", the
    /// block's own word for its state (stateLabel) instead of the state's, the dependencies by <see cref="DependencyNames"/>.</summary>
    public static string Meta(JsonNode node, IReadOnlyDictionary<string, string> names)
    {
        var state = Value(node, "state");
        var direction = Directions.GetValueOrDefault(Value(node, "direction"), Directions["internal"]);
        var date = Value(node, "date");
        var party = Value(node, "party");
        var system = Value(node, "system");
        var where = Value(node, "where");
        var key = Value(node, "key");
        var requires = Value(node, "requires");
        return string.Join(" · ", new[]
        {
            (Value(node, "stateLabel") is { Length: > 0 } label ? label : States.GetValueOrDefault(state, state)) + (date.Length > 0 ? " " + Date(date) : ""),
            direction.Label,
            party.Length > 0 ? direction.Party + ": " + party : "",
            system.Length > 0 ? "služba: " + system : "",
            where.Length > 0 ? "uloženo: " + where : "",
            key.Length > 0 ? "klíč: " + key : "",
            requires.Length > 0 ? "závisí na: " + string.Join(", ", requires.Split(',').Select(x => names.GetValueOrDefault(x, x))) : "",
        }.Where(x => x.Length > 0));
    }

    /// <summary>
    /// "57 % hotovo (12 z 21) · čeká na: SMS brána (IT KBN) · nedodané vstupy: SMS brána (IT KBN) · lze nasadit: Core · čeká na 2 prerekvizity: NIA"
    /// over every requirement of the document: whom it waits on (waiting for input only, not our work in progress) apart from the inputs
    /// not delivered, then what can go on (<see cref="Readiness"/>).
    /// </summary>
    public static string Summary(JsonNode document)
    {
        var counted = Requirements(document).Where(x => Value(x, "state") != "not_applicable").ToList();
        var done = counted.Count(IsDone);
        var waiting = counted.Where(x => Value(x, "state") == "waiting_for_input").ToList();
        var missing = counted.Where(x => Value(x, "direction") == "inbound" && !IsDone(x)).ToList();
        var percent = counted.Count > 0 ? done * 100 / counted.Count : 0;
        return string.Create(CultureInfo.InvariantCulture, $"{percent} % hotovo ({done} z {counted.Count})")
            + (waiting.Count > 0 ? " · čeká na: " + Named(waiting) : "")
            + (missing.Count > 0 ? " · nedodané vstupy: " + Named(missing) : "")
            + Readiness(Requirements(document).ToList());
    }

    /// <summary>The order of the readiness groups in the summary; "čeká na N prerekvizit" goes between, by N, and "blokováno" last.</summary>
    private static readonly string[] ReadinessOrder = ["lze nasadit", "lze předat", "čeká na dodání", "čeká na vstup"];

    /// <summary>" · lze nasadit: A, B · čeká na dodání: C · čeká na 2 prerekvizity: D · blokováno: E": the unfinished blocks grouped by their
    /// readiness, five titles each and then how many more, with the web's words (requirementReadiness.ts, review V1, V2): blocked when it
    /// is or a prerequisite it waits for is, however deep; else waiting on its unfinished prerequisites (its <c>requires</c> on the page);
    /// else waiting for input; else by direction, an input "čeká na dodání", our work "lze nasadit", a hand-over "lze předat".</summary>
    private static string Readiness(IReadOnlyList<JsonNode> blocks)
    {
        static bool Finished(JsonNode block) => Value(block, "state") is "done" or "not_applicable";
        // As the web's keyed() and awaitedBy(): a repeated key names its last block, the block's own key is no prerequisite.
        var byKey = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var block in blocks.Where(x => Value(x, "key").Length > 0))
        {
            byKey[Value(block, "key")] = block;
        }
        List<JsonNode> Awaited(JsonNode block) => Value(block, "requires").Split(',')
            .Where(key => key != Value(block, "key")).Select(key => byKey.GetValueOrDefault(key)).OfType<JsonNode>().Where(x => !Finished(x)).Distinct().ToList();
        var blocked = new Dictionary<JsonNode, bool>(ReferenceEqualityComparer.Instance);
        bool Blocked(JsonNode block)
        {
            if (blocked.TryGetValue(block, out var known))
            {
                return known;
            }
            // A block on the walk reads as not blocked while it is walked: the end of a cycle.
            blocked[block] = false;
            return blocked[block] = Value(block, "state") == "blocked" || !Finished(block) && Awaited(block).Any(Blocked);
        }
        var groups = new Dictionary<string, (int Rank, List<string> Titles)>(StringComparer.Ordinal);
        foreach (var block in blocks.Where(x => !Finished(x)))
        {
            var prerequisites = Awaited(block).Count;
            var (label, rank) = Blocked(block) ? ("blokováno", int.MaxValue)
                : prerequisites > 0 ? (string.Create(CultureInfo.InvariantCulture, $"čeká na {prerequisites} {(prerequisites == 1 ? "prerekvizitu" : prerequisites <= 4 ? "prerekvizity" : "prerekvizit")}"),
                    ReadinessOrder.Length + prerequisites)
                : Value(block, "state") == "waiting_for_input" ? ("čeká na vstup", 3)
                : Value(block, "direction") switch { "inbound" => ("čeká na dodání", 2), "internal" => ("lze nasadit", 0), _ => ("lze předat", 1) };
            if (!groups.TryGetValue(label, out var group))
            {
                groups[label] = group = (rank, []);
            }
            group.Titles.Add(Title(block) is { Length: > 0 } title ? title : Value(block, "key") is { Length: > 0 } key ? key : "bez názvu");
        }
        return string.Concat(groups.OrderBy(group => group.Value.Rank).Select(group => $" · {group.Key}: {string.Join(", ", group.Value.Titles.Take(5))}"
            + (group.Value.Titles.Count > 5 ? string.Create(CultureInfo.InvariantCulture, $" +{group.Value.Titles.Count - 5}") : "")));
    }

    private static string Named(IEnumerable<JsonNode> blocks)
        => string.Join(", ", blocks.Select(x => Title(x) + (Value(x, "party") is { Length: > 0 } party ? " (" + party + ")" : "")));

    /// <summary>
    /// The notes of a requirement block (everything under its title: text, components, nested blocks) as page Markdown with placeholders
    /// (<see cref="RichMarkdownProjection"/>), numbered from 1 within the block: what <see cref="ReplaceNotes"/> takes back.
    /// </summary>
    public static ContentProjection Notes(JsonObject rich, JsonObject block, ProjectionOptions options)
        => RichMarkdownProjection.Project(NotesDocument(rich, block), options);

    /// <summary>
    /// Replaces the notes of <paramref name="block"/> in <paramref name="rich"/> with <paramref name="markdown"/> as
    /// <see cref="RichMarkdownMerge.ReplaceAll"/> merges a page: an unchanged paragraph keeps its JSON, every placeholder of
    /// <see cref="Notes"/> brings its node back (a nested block as it was), a placeholder left out is 409 <c>components_removed</c>.
    /// The page names the plugins the new notes use.
    /// </summary>
    // shortcut: page links in the notes are not resolved ([[Název]] and links to pages new to the block stay text); resolve them on the
    // actor's rights as PreparePageMarkdownSave does when notes need new page links.
    public static void ReplaceNotes(JsonObject rich, JsonObject block, string markdown)
    {
        var notes = NotesDocument(rich, block);
        var context = new MergeContext(_ => PageLinkResolution.None, _ => false, () => Guid.NewGuid().ToString());
        var merged = JsonNode.Parse(RichMarkdownMerge.ReplaceAll(notes, RichMarkdownProjection.Project(notes, new ProjectionOptions(0)), markdown, context).ContentJson,
            documentOptions: MarkdownDocumentValidator.JsonOptions)!;
        var blocks = merged["document"]!["content"]!.AsArray().Select(x => x!.DeepClone()).ToList();
        // The merge ends a document with a paragraph, as the editor does; in a block the notes end where they end.
        while (blocks is [.., JsonObject last] && last["type"]?.GetValue<string>() == "paragraph" && last["content"] is not JsonArray { Count: > 0 })
        {
            blocks.RemoveAt(blocks.Count - 1);
        }
        var content = block["content"] as JsonArray ?? [];
        block["content"] = new JsonArray([content.Count > 0 ? content[0]!.DeepClone() : new JsonObject { ["type"] = "paragraph" }, .. blocks]);
        RichMarkdownMerge.DeclarePlugins(rich, block);
    }

    private static RichDocument NotesDocument(JsonObject rich, JsonObject block)
    {
        var content = block["content"] as JsonArray ?? [];
        return RichDocument.Parse(new JsonObject
        {
            ["format"] = MarkdownDocumentValidator.Format, ["version"] = 1, ["plugins"] = rich["plugins"]?.DeepClone() ?? new JsonArray(),
            ["document"] = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(content.Skip(1).Select(x => x?.DeepClone()).ToArray()) },
        }.ToJsonString());
    }

    /// <summary>Every requirement block, nested ones included, in document order; in a document (<c>doc</c>) a requirement heading reads
    /// as its block (<see cref="RequirementHeadings.View"/>, its dependencies relative to <paramref name="pageId"/>).</summary>
    public static IEnumerable<JsonNode> Requirements(JsonNode node, Guid? pageId = null)
    {
        if (node["type"]?.GetValue<string>() == "requirement")
        {
            yield return node;
        }
        foreach (var child in node["type"]?.GetValue<string>() == "doc" ? RequirementHeadings.Project(node, pageId) : (IEnumerable<JsonNode?>)(node["content"] as JsonArray ?? []))
        {
            if (child is null)
            {
                continue;
            }
            foreach (var requirement in Requirements(child))
            {
                yield return requirement;
            }
        }
    }

    /// <summary>
    /// Every requirement block in document order with its section, the one rule of the page's sections (the web's outline,
    /// src/Atlas.Web/src/projector/requirementOverview.ts): <c>Section</c> the texts of the headings above it, outermost first (a heading
    /// closes the ones of its level and deeper); <c>Heading</c> the place of its nearest heading among the page's headings, -1 before the
    /// first. A heading inside a block starts no section, and a nested block is in its top-level block's. A requirement heading is a block,
    /// no section: it is in the section of the plain headings above it.
    /// </summary>
    public static IReadOnlyList<(JsonNode Block, IReadOnlyList<string> Section, int Heading)> Sectioned(JsonNode document, Guid? pageId = null)
    {
        var blocks = new List<(JsonNode, IReadOnlyList<string>, int)>();
        var open = new List<(string Text, int Level)>();
        IReadOnlyList<string> section = [];
        var (heading, headings) = (-1, 0);
        void Walk(IEnumerable<JsonNode?> nodes)
        {
            foreach (var child in nodes)
            {
                switch (child?["type"] is JsonValue type && type.TryGetValue<string>(out var name) ? name : null)
                {
                    case "heading":
                        var level = child!["attrs"]?["level"] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 1;
                        open.RemoveAll(entry => entry.Level >= level);
                        open.Add((PlainText(child).Trim(), level));
                        (section, heading) = (open.Select(entry => entry.Text).ToArray(), headings++);
                        break;
                    case "requirement":
                        blocks.AddRange(Requirements(child!).Select(block => (block, section, heading)));
                        break;
                    case not null:
                        Walk(child!["content"] as JsonArray ?? []);
                        break;
                }
            }
        }
        Walk(RequirementHeadings.Project(document, pageId));
        return blocks;
    }

    private static string PlainText(JsonNode? node)
        => node?["text"] is JsonValue text && text.TryGetValue<string>(out var value) ? value : string.Concat((node?["content"] as JsonArray ?? []).Select(PlainText));

    /// <summary>2026-09-10 → 10. 9. 2026; the schema allows only this digit pattern.</summary>
    private static string Date(string date)
    {
        var parts = date.Split('-');
        return string.Create(CultureInfo.InvariantCulture, $"{int.Parse(parts[2], CultureInfo.InvariantCulture)}. {int.Parse(parts[1], CultureInfo.InvariantCulture)}. {parts[0]}");
    }
}
