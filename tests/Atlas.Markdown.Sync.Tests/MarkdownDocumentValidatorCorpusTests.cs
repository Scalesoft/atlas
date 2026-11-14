using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>
/// The server validator against the editor's own verdicts. packages/markdown/src/schema/manifest.test.ts generates thousands of
/// variants of every node, mark, attribute and component data field (boundaries, wrong types, bad URLs, plugin versions, table
/// geometry), opens each through the editor's real load path and keeps Fixtures/document-schema/verdicts.json equal to a reduced
/// set of the results. <see cref="MarkdownDocumentValidator"/> must accept exactly what the editor accepts, apart from
/// <see cref="Deliberate"/>.
/// </summary>
public sealed class MarkdownDocumentValidatorCorpusTests
{
    /// <summary>
    /// Where the server deliberately differs from the editor (the manifest's <c>enforcement: "server"</c> rules and the walk's
    /// bounds), by case id or by an id prefix ending in "/" (every probe of one attribute). An entry only excuses that direction,
    /// and an entry that excuses no case fails the test, so the list stays exact.
    /// </summary>
    private static readonly (string Id, bool ServerStricter, string Reason)[] Deliberate =
    [
        // Attributes the editor stores without a validator (StarterKit, tiptap-markdown, Tiptap Image and Link): the server
        // requires the JSON type and shape the editor itself writes, which server-side readers (Markdown projection, search,
        // export) rely on. Only an API client can send another type.
        ("nodes.heading.attrs.level/", true, "the editor's levels 1–6 only (HEADING_LEVELS): Tiptap stores any level, and the "
            + "Markdown projection writes the level as that many '#' (a level in the billions exhausts the heap)"),
        ("nodes.emoji.attrs.name/", true, "text"),
        ("nodes.bulletList.attrs.tight/", true, "boolean"),
        ("nodes.timedList.attrs.tight/", true, "boolean"),
        ("nodes.orderedList.attrs.tight/", true, "boolean"),
        ("nodes.orderedList.attrs.start/", true, "a JavaScript-safe integer (paste gives what parseInt reads)"),
        ("nodes.orderedList.attrs.type/", true, "single-line text of at most 100 characters (an HTML list type such as \"a\")"),
        ("nodes.taskItem.attrs.checked/", true, "boolean"),
        ("nodes.codeBlock.attrs.language/", true, "single-line text of at most 200 characters (a fence's language name)"),
        ("nodes.image.attrs.alt/", true, "text"),
        ("nodes.image.attrs.title/", true, "text"),
        ("nodes.image.attrs.width/", true, "a number or short text, as pasted HTML gives it"),
        ("nodes.image.attrs.height/", true, "a number or short text, as pasted HTML gives it"),
        ("marks.link.attrs.target/", true, "text"),
        ("marks.link.attrs.rel/", true, "text"),
        ("marks.link.attrs.class/", true, "text"),
        ("marks.link.attrs.title/", true, "text"),
        ("marks.link.attrs.href/type-number", true, "text (the editor skips a falsy href and crashes on other numbers)"),
        // Script links: the editor opens a stored href of any scheme (isAllowedUri filters only pasted and rendered links), and
        // so does the server, except the script-capable schemes however disguised (Tiptap's whitespace stripping included).
        ("marks.link.attrs.href/url-javascript", true, "script link"),
        ("marks.link.attrs.href/url-javascript-upper", true, "script link"),
        ("marks.link.attrs.href/url-javascript-spaced", true, "script link"),
        ("marks.link.attrs.href/url-javascript-split", true, "script link"),
        ("marks.link.attrs.href/url-javascript-ideographic-space", true, "script link"),
        ("marks.link.attrs.href/url-javascript-nbsp", true, "script link"),
        ("marks.link.attrs.href/url-vbscript", true, "script link"),
        ("marks.link.attrs.href/url-data-html", true, "data: link"),
        ("marks.link.attrs.href/url-data-image", true, "data: link"),
        ("marks.link.attrs.href/url-data-svg", true, "data: link"),
        // The walk is recursive; MarkdownDocumentValidator.MaxDepth bounds it (31 nested list levels) and every reader's JSON depth.
        ("structure.depth/64", true, "deeper than 64 nodes"),
        ("structure.depth/65", true, "deeper than 64 nodes"),
        // Shapes Atlas 2.x stored that the editor repairs when it opens them (engine/legacyDocument.ts); a write must already be
        // repaired, and the archive import repairs before it validates (LegacyDocumentRepair).
        ("nodes.listItem.content/empty", true, "an empty list item gets its leading paragraph on open"),
        ("nodes.listItem.content/missing", true, "an empty list item gets its leading paragraph on open"),
        ("nodes.taskItem.content/empty", true, "an empty task item gets its leading paragraph on open"),
        ("nodes.taskItem.content/missing", true, "an empty task item gets its leading paragraph on open"),
        ("nodes.timedItem.content/empty", true, "an empty timed item gets its leading paragraph on open"),
        ("nodes.timedItem.content/missing", true, "an empty timed item gets its leading paragraph on open"),
        ("structure.list/item-heading-first", true, "a list item starting with another block gets a leading paragraph on open"),
        ("marks.link+link", true, "a repeated mark keeps its innermost copy on open"),
        ("marks.bold+bold", true, "a repeated mark keeps its innermost copy on open"),
        ("marks.code+code", true, "a repeated mark keeps its innermost copy on open"),
        ("nodes.contactCard.attrs.data.contacts/items-202", true, "a card of several contacts becomes single-contact cards on open"),
    ];

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "document-schema", name);

    [Fact]
    public void Accepts_exactly_what_the_editor_opens()
    {
        var cases = JsonNode.Parse(File.ReadAllText(Fixture("verdicts.json")), null, new System.Text.Json.JsonDocumentOptions { MaxDepth = 1_000 })!.AsArray();
        Assert.True(cases.Count > 500, "verdicts.json holds the generated corpus");
        var documents = cases.Where(item => item!["patch"] is null).ToDictionary(item => (string)item!["id"]!, item => item!["document"]);
        var failures = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in cases)
        {
            var id = (string)item!["id"]!;
            var editorAccepts = (bool)item["editorAccepts"]!;
            var violation = MarkdownDocumentValidator.Builtin.Validate(Expand(Document(item, documents))!.ToJsonString());
            if (violation is null == editorAccepts) continue;
            var excuse = Deliberate.FirstOrDefault(entry => (entry.Id == id || (entry.Id.EndsWith('/') && id.StartsWith(entry.Id, StringComparison.Ordinal)))
                && entry.ServerStricter == editorAccepts);
            if (excuse.Id is not null)
            {
                used.Add(excuse.Id);
                continue;
            }
            failures.Add(editorAccepts
                ? $"{id}: the editor opens it, the server refuses it: {violation}"
                : $"{id}: the editor refuses it ({item["editorError"]}), the server accepts it");
        }
        failures.AddRange(Deliberate.Where(entry => !used.Contains(entry.Id)).Select(entry => $"{entry.Id}: listed as deliberate but no case differs that way; remove it"));
        Assert.True(failures.Count == 0, $"{failures.Count} of {cases.Count} corpus cases disagree with the editor:\n{string.Join("\n", failures.Take(200))}");
    }

    [Fact]
    public void Accepts_the_document_the_editor_saves_with_every_node_and_mark()
    {
        var violation = MarkdownDocumentValidator.Builtin.Validate(File.ReadAllText(Fixture("web-document.json")));
        Assert.Null(violation);
    }

    /// <summary>A case's document: stored whole, or its base's document with the patch applied (see packCorpus in the editor test).</summary>
    private static JsonNode? Document(JsonNode? item, Dictionary<string, JsonNode?> documents)
    {
        if (item!["patch"] is not { } patch) return item["document"];
        // Without a named base: "nodes.<type>/valid" for an id that starts "nodes.<type>.".
        var baseId = (string?)item["base"] ?? $"{Regex.Match((string)item["id"]!, @"^nodes\.\w+").Value}/valid";
        var result = documents[baseId]!.DeepClone();
        foreach (var change in patch.AsArray())
        {
            var path = ((string)change![0]!).Split('/');
            var owner = result;
            foreach (var key in path[..^1]) owner = owner is JsonArray list ? list[int.Parse(key)] : owner![key];
            var remove = change[1] is JsonObject marker && marker["$absent"] is not null;
            var value = remove ? null : change[1]?.DeepClone();
            if (owner is JsonArray array) array[int.Parse(path[^1])] = value;
            else if (remove) owner!.AsObject().Remove(path[^1]);
            else owner![path[^1]] = value;
        }
        return result;
    }

    /// <summary>The fixture's compact forms ({"$string", "pad", "length"} and {"$array", "count"}), as the editor test expands them.</summary>
    private static JsonNode? Expand(JsonNode? value) => value switch
    {
        JsonArray array => new JsonArray(array.Select(Expand).ToArray()),
        JsonObject text when text["$string"] is { } prefix
            => JsonValue.Create((string)prefix! + new string(((string)text["pad"]!)[0], (int)text["length"]! - ((string)prefix!).Length)),
        JsonObject list when list.ContainsKey("$array")
            => new JsonArray(Enumerable.Range(0, (int)list["count"]!).Select(_ => Expand(list["$array"])).ToArray()),
        JsonObject obj => new JsonObject(obj.Select(x => KeyValuePair.Create(x.Key, Expand(x.Value)))),
        _ => value?.DeepClone(),
    };
}
