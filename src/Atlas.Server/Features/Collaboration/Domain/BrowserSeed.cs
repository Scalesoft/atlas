using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Markdown.Sync;
using Atlas.Server.Platform.Content;
using static Atlas.Server.Features.Collaboration.Domain.SeedNodes;

namespace Atlas.Server.Features.Collaboration.Domain;

public static partial class BrowserSeed
{
    /// <summary>
    /// The document the browser's seed writes (SignalRYjsProvider.seedInitialContent; the Node authority's <c>seedRich</c>): the stored
    /// document repaired as the editor opens it, then set over the empty page in one transaction, after which every plugin's
    /// appendTransaction runs until none appends more. Those that act on such a transaction, in the editor's plugin order:
    /// <list type="bullet">
    /// <item>the chart references (plugins/charts/source.ts ensureChartReferences and refreshChartSource): one id per table part, linked
    /// charts' data read from their table; again after the table repair, as the next round does;</item>
    /// <item>the requirement rules (src/Atlas.Web/src/projector/requirementHeadingView.ts Rules) for a whole document inserted: emptied
    /// dependency lines go, dependency lines are cleared or keep only their links, nested requirements are plain headings, repeated
    /// heading ids are fresh;</item>
    /// <item>prosemirror-tables 1.8.5 fixTables (MarkdownTable's tableEditing): <see cref="BrowserSeedTables"/>;</item>
    /// <item>TrailingNode (@tiptap/extensions 3.31.3, in StarterKit): a document that does not end with a paragraph gets an empty one.</item>
    /// </list>
    /// The others do nothing here: Link's autolink (the seed carries <c>preventAutolink</c>: opening a page links no bare URL), the heading anchors and recognition skip a read-only editor (the authority's), paste rules, chip text,
    /// CharacterCount (no limit), ClearDocument and the markdown-safe table commands (not in the rich profile) skip this transaction.
    /// Mark order and attribute defaults are the writer's (Node.fromJSON).
    /// </summary>
    /// <exception cref="InvalidDataException">A chart's data is invalid (normalizeChart throws in the editor too).</exception>
    public static JsonObject Prepare(JsonObject stored)
    {
        LegacyDocumentRepair.Repair(stored);
        if (stored["content"] is not JsonArray content)
        {
            stored["content"] = content = [];
        }
        EnsureChartReferences(stored);
        RefreshCharts(stored);
        RequirementRules(content);
        BrowserSeedTables.Apply(stored);
        EnsureChartReferences(stored);
        RefreshCharts(stored);
        // TrailingNode
        if (content.Count == 0 || Type(content[^1]) != "paragraph")
        {
            content.Add(new JsonObject { ["type"] = "paragraph" });
        }
        return stored;
    }

    // ——— charts/source.ts ———

    private static readonly HashSet<string> ChartReferenceTypes = new(StringComparer.Ordinal) { "table", "tableRow", "tableCell", "tableHeader" };

    private static string? StringAttr(JsonNode? node, string name) => node?["attrs"]?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>ensureChartReferences without a previous document or a chart being made: every table, row and cell in document order
    /// keeps its id unless an earlier one has it, which drops it; one without an id gets none.</summary>
    private static void EnsureChartReferences(JsonNode root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode node)
        {
            foreach (var child in Content(node) ?? [])
            {
                if (ChartReferenceTypes.Contains(Type(child)) && StringAttr(child, "chartRef") is { Length: > 0 } id && !seen.Add(id))
                {
                    child!["attrs"]!["chartRef"] = null;
                }
                Walk(child!);
            }
        }
        Walk(root);
    }

    /// <summary>The chart plugin's appendTransaction after the ids: each chart's data as refreshChartSource reads it from the document,
    /// written only when that differs from normalizeChart of it.</summary>
    private static void RefreshCharts(JsonNode root)
    {
        void Walk(JsonNode node)
        {
            foreach (var child in Content(node) ?? [])
            {
                if (Type(child) == "chart")
                {
                    var data = NormalizeChart(child!["attrs"]?["data"]);
                    var next = RefreshChartSource(data, root);
                    if (!JsonNode.DeepEquals(next, data))
                    {
                        ((JsonObject)child["attrs"]!)["data"] = next;
                    }
                }
                Walk(child!);
            }
        }
        Walk(root);
    }

    // refreshChartSource
    private static JsonObject RefreshChartSource(JsonObject data, JsonNode doc)
    {
        if (data["source"] is not JsonObject source)
        {
            return data;
        }
        var tableId = source["tableId"]!.GetValue<string>();
        JsonNode? table = null;
        void Find(JsonNode node)
        {
            foreach (var child in Content(node) ?? [])
            {
                if (Type(child) == "table" && StringAttr(child, "chartRef") == tableId) table = child;
                else Find(child!);
            }
        }
        Find(doc);
        if (table is null)
        {
            return With(data, "missing");
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        void Cells(JsonNode node)
        {
            foreach (var child in Content(node) ?? [])
            {
                if (Type(child) is "tableCell" or "tableHeader")
                {
                    if (Span(child!, "colspan") == 1 && Span(child!, "rowspan") == 1 && StringAttr(child, "chartRef") is { } id) values[id] = TextContent(child);
                }
                else Cells(child!);
            }
        }
        Cells(table);
        var cells = source["cells"]!.AsArray().Select(row => row!.AsArray().Select(id => id!.GetValue<string>()).ToArray()).ToArray();
        if (cells.Any(row => row.Any(id => !values.ContainsKey(id))))
        {
            return With(data, "invalid");
        }
        var linked = (JsonObject)data.DeepClone();
        linked["sourceStatus"] = "linked";
        linked["grid"] = new JsonArray([.. cells.Select(row => (JsonNode)new JsonArray([.. row.Select(id => (JsonNode)values[id])]))]);
        try
        {
            return NormalizeChart(linked);
        }
        catch (InvalidDataException)
        {
            return With(data, "invalid");
        }
    }

    private static int Span(JsonNode cell, string name)
        => cell["attrs"] is not JsonObject attrs || !attrs.TryGetPropertyValue(name, out var value) ? 1
            : JsNumber(value) == 1 ? 1 : 0;

    private static JsonObject With(JsonObject data, string status)
    {
        var copy = (JsonObject)data.DeepClone();
        copy["sourceStatus"] = status;
        return copy;
    }

    // ——— charts/data.ts ———

    private static readonly string[] ChartKinds = ["bar", "line", "pie", "doughnut"];

    /// <summary>normalizeChart: the data validated, in its canonical shape and key order.</summary>
    private static JsonObject NormalizeChart(JsonNode? value)
    {
        static InvalidDataException Invalid(string message) => new($"[CHART] {message}");
        if (value is not JsonObject data) throw Invalid("Neplatná data grafu.");
        if (JsNumber(data["version"]) != 1 || data["kind"] is not JsonValue kindValue || !kindValue.TryGetValue<string>(out var kind) || !ChartKinds.Contains(kind))
        {
            throw Invalid("Neplatná verze nebo druh grafu.");
        }
        if (data["title"] is not JsonValue titleValue || !titleValue.TryGetValue<string>(out var title) || title.Length > 300) throw Invalid("Název grafu je příliš dlouhý.");
        var grid = ValidateChartGrid(data["grid"]);
        if (kind is "pie" or "doughnut" && grid.Skip(1).Any(row => row.Skip(1).Any(cell => (ParseChartNumber(cell) ?? 0) < 0))) throw Invalid("Záporné hodnoty nejsou povoleny.");
        JsonObject? source = null;
        if (data["source"] is { } rawSource)
        {
            static bool ValidId(JsonNode? id) => id is JsonValue value && value.TryGetValue<string>(out var text) && text.Length is > 0 and <= 100;
            if (rawSource is not JsonObject raw || !ValidId(raw["tableId"]) || raw["cells"] is not JsonArray rows || rows.Count != grid.Length
                || rows.Any(row => row is not JsonArray ids || ids.Count != grid[0].Length || ids.Any(id => !ValidId(id))))
            {
                throw Invalid("Neplatný odkaz na tabulku.");
            }
            var ids = rows.SelectMany(row => row!.AsArray()).Select(id => id!.GetValue<string>()).ToArray();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw Invalid("Sloučené buňky nejsou podporovány.");
            source = new JsonObject { ["tableId"] = raw["tableId"]!.GetValue<string>(), ["cells"] = rows.DeepClone() };
        }
        var status = data["sourceStatus"];
        var statusText = status is JsonValue statusValue && statusValue.TryGetValue<string>(out var text) ? text : null;
        if (source is not null ? statusText is not ("linked" or "missing" or "invalid") : status is not null) throw Invalid("Neplatný stav zdroje.");
        return new JsonObject
        {
            ["version"] = 1, ["title"] = title, ["kind"] = kind,
            ["grid"] = new JsonArray([.. grid.Select(row => (JsonNode)new JsonArray([.. row.Select(cell => (JsonNode)cell)]))]),
            ["source"] = source, ["sourceStatus"] = source is not null ? statusText : null,
        };
    }

    // validateChartGrid
    private static string[][] ValidateChartGrid(JsonNode? value)
    {
        if (value is not JsonArray rows || rows.Count is < 2 or > 201) throw new InvalidDataException("[CHART] Graf potřebuje záhlaví a řádky.");
        var width = rows[0] is JsonArray first ? first.Count : 0;
        if (width is < 2 or > 21) throw new InvalidDataException("[CHART] Graf potřebuje popisky a řady.");
        return [.. rows.Select((row, r) =>
        {
            if (row is not JsonArray cells || cells.Count != width) throw new InvalidDataException("[CHART] Řádky musí mít stejnou šířku.");
            return cells.Select((cell, c) =>
            {
                if (cell is not JsonValue cellValue || !cellValue.TryGetValue<string>(out var text) || text.Length > 2000) throw new InvalidDataException("[CHART] Buňka je příliš dlouhá.");
                if (r > 0 && c > 0) ParseChartNumber(text);
                return text;
            }).ToArray();
        })];
    }

    // parseChartNumber
    private static double? ParseChartNumber(string value)
    {
        var trimmed = JsTrim(value);
        if (trimmed.Length == 0) return null;
        if (!ChartNumber().IsMatch(trimmed)) throw new InvalidDataException("[CHART] Není číslo.");
        var number = double.Parse(trimmed.Replace(" ", "", StringComparison.Ordinal).Replace("\u00a0", "", StringComparison.Ordinal).Replace("\u202f", "", StringComparison.Ordinal)
            .Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number)) throw new InvalidDataException("[CHART] Číslo je příliš velké.");
        return number;
    }

    [GeneratedRegex(@"^[+-]?(?:[0-9]+|[0-9]{1,3}(?:[ \u00a0\u202f][0-9]{3})+)(?:[,.][0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChartNumber();

    private static string JsTrim(string text)
    {
        int start = 0, end = text.Length;
        while (start < end && IsJsSpace(text[start])) start++;
        while (end > start && IsJsSpace(text[end - 1])) end--;
        return text[start..end];
    }

    // ——— requirementHeadingView.ts Rules, for the whole document inserted at once (no host page: no link is the page's own) ———

    private static bool IsRequirementHeading(JsonNode? node) => Type(node) == "heading" && node!["attrs"]?["state"] is { } state && state.GetValueKind() != JsonValueKind.Null;

    private static bool IsDependencyLine(JsonNode? node) => Type(node) == "paragraph" && node!["attrs"]?["dependencies"] is JsonValue flag && flag.GetValueKind() == JsonValueKind.True;

    private static bool IsSeparators(string text) => text.All(c => c == ',' || IsJsSpace(c));

    private static bool IsLinkOrSeparator(JsonNode? node) => Type(node) == "pageReference" || (IsText(node) && IsSeparators(Text(node)));

    private static bool IsFactsLine(JsonNode? node) => Type(node) == "paragraph" && RequirementHeadings.DeadlineOf(node) is not null;

    private static readonly (string Name, JsonNode? Value)[] RequirementOnly =
        [("state", null), ("stateLabel", ""), ("direction", "internal"), ("system", ""), ("party", ""), ("partyContactId", ""), ("from", "")];

    /// <returns>The headings given a fresh id (setNodeMarkup, whose ranges the next round's autolink reads).</returns>
    private static HashSet<JsonNode> RequirementRules(JsonArray content)
    {
        var renamed = new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
        // A dependency line without links or text goes.
        foreach (var line in content.Where(node => IsDependencyLine(node) && !(Content(node) ?? []).Any(child => Type(child) == "pageReference")
                     && IsSeparators(TextContent(node))).ToArray())
        {
            content.Remove(line);
        }
        var edits = new List<(int Index, string Edit)>();
        JsonNode? previous = null;
        for (var i = 0; i < content.Count; i++)
        {
            var node = content[i];
            if (IsDependencyLine(node))
            {
                var children = Content(node) ?? [];
                bool links = children.Any(child => Type(child) == "pageReference"), rest = children.Any(child => !IsLinkOrSeparator(child));
                if (!IsRequirementHeading(previous) || (!links && rest)) edits.Add((i, "clear"));
                else if (rest) edits.Add((i, "extract"));
            }
            previous = node;
        }
        // Nested: a requirement heading is a plain heading, a dependency line is cleared.
        void Nested(JsonNode node)
        {
            foreach (var child in Content(node) ?? [])
            {
                if (IsRequirementHeading(child))
                {
                    var attrs = (JsonObject)child!["attrs"]!;
                    foreach (var (name, value) in RequirementOnly) attrs[name] = value?.DeepClone();
                }
                else if (IsDependencyLine(child))
                {
                    Clear(child!);
                }
                if (!IsTextblock(child)) Nested(child!);
            }
        }
        foreach (var block in content) Nested(block!);
        // A heading id the document repeats: every copy after the first gets a fresh id and no aliases.
        foreach (var copies in content.Where(node => Type(node) == "heading" && StringAttr(node, "headingId") is { Length: > 0 })
                     .GroupBy(node => StringAttr(node, "headingId"), StringComparer.Ordinal))
        {
            foreach (var copy in copies.Skip(1))
            {
                copy!["attrs"]!["headingId"] = Guid.NewGuid().ToString("D");
                copy["attrs"]!["aliases"] = "";
                renamed.Add(copy);
            }
        }
        foreach (var (index, edit) in edits.OrderByDescending(entry => entry.Index))
        {
            if (edit == "clear") Clear(content[index]!);
            else Extract(content, index);
        }
        return renamed;
    }

    private static void Clear(JsonNode line)
    {
        line["attrs"]!["dependencies"] = false;
        // A line of links split in the middle: the note starts with its link, not with the „, “ between them.
        if (Content(line) is { Count: > 1 } inline && IsText(inline[0]) && Text(inline[0]).Length > 0 && IsSeparators(Text(inline[0])))
        {
            inline.RemoveAt(0);
        }
    }

    /// <summary>Text in a dependency line goes to a note after the requirement's lines; the line keeps its links, or goes without any.</summary>
    private static void Extract(JsonArray content, int index)
    {
        var line = content[index]!;
        var links = new List<JsonNode>();
        var rest = new List<JsonNode>();
        foreach (var child in Content(line) ?? [])
        {
            if (Type(child) == "pageReference") links.Add(child!.DeepClone());
            else if (!IsLinkOrSeparator(child))
            {
                var copy = child!.DeepClone();
                if (IsText(copy)) copy["text"] = TrimSeparators(Text(copy));
                rest.Add(copy);
            }
        }
        var at = index + 1 < content.Count && IsFactsLine(content[index + 1]) ? index + 2 : index + 1;
        if (rest.Count > 0) content.Insert(at, new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray([.. rest]) });
        if (links.Count > 0) line["content"] = new JsonArray([.. links.SelectMany((link, i) => i > 0 ? [new JsonObject { ["type"] = "text", ["text"] = ", " }, link] : new[] { link })]);
        else content.RemoveAt(index);
    }

    private static string TrimSeparators(string text)
    {
        int start = 0, end = text.Length;
        while (start < end && (text[start] == ',' || IsJsSpace(text[start]))) start++;
        while (end > start && (text[end - 1] == ',' || IsJsSpace(text[end - 1]))) end--;
        return text[start..end];
    }
}
