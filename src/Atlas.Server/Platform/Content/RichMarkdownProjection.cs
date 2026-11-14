using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// Projects a rich document to Markdown with component placeholders (plan 2026-10-05 MCP §9 K1, D8), on the principle of
/// the editor's page source session (<c>@atlas/markdown/source</c>) but finer: a top-level block is text only when
/// <c>MarkdownToRich.Parse(EditorMarkdownWriter.WriteProjection(block))</c> gives the same block back
/// (<see cref="RichDocument.StructurallyEqual"/>), and an inline component inside such a block is an inline placeholder,
/// so a paragraph mentioning a contact stays editable. Every other block is one block placeholder.
/// <para>
/// The output never exposes what Markdown cannot guard: no password directive (<c>:secret</c> anywhere makes the block a
/// placeholder), no unescaped placeholder bracket outside a real placeholder (code that holds one is a placeholder), no
/// link outside <see cref="MarkdownToRich.SafeHref"/> (it fails the round trip). Labels carry no password metadata, URL or
/// unreadable page title. Empty paragraphs have no Markdown form and are left out. Pure and deterministic; references the
/// reader may not see must be removed before (<c>SecretReferenceAccess.ForReaderAsync</c>).
/// </para>
/// <para>
/// An image of the page's own attachments (<c>attrs.src</c> the canonical <c>/api/attachments/{id}/content</c>) is text, at the top
/// level or inside a quote, callout, list or cell: <c>![alt](/api/attachments/{id}/content "title")</c>. It is compared as Markdown
/// sees it (<see cref="MarkdownToRich.ImageNode"/>: address, alternative text, title); size and presentation stay in the stored node,
/// which the merge gives back to an unchanged image. Any other image (a remote or <c>data:</c> address) stays a placeholder, so
/// Markdown never creates one.
/// </para>
/// <para>
/// Presentation Markdown cannot show (<see cref="Presentation"/>: a paragraph's or heading's alignment, cell colours and column
/// widths, row heights, a list's numbering style) does not make a block a placeholder: it is left out of the comparison and the
/// merge keeps it from the stored node.
/// </para>
/// </summary>
public static partial class RichMarkdownProjection
{
    /// <summary>Characters of output a single read returns; above it the client reads by sections (C1).</summary>
    public const int OutputLimit = 200_000;

    private const int MaxLabelText = 80;

    // Block types the dialect carries, at the top level or nested; any other node at a block position makes its top-level
    // block a placeholder.
    private static readonly HashSet<string> TextBlocks = new(StringComparer.Ordinal)
    {
        "paragraph", "heading", "blockquote", "callout", "bulletList", "orderedList", "taskList", "timedList", "listItem", "taskItem", "timedItem",
        "codeBlock", "horizontalRule", "table", "tableRow", "tableHeader", "tableCell", "image", "mathBlock", "diagram", "contactCard", "chart", "overview",
    };

    private static readonly HashSet<string> TopLevelText = new(StringComparer.Ordinal)
    {
        "paragraph", "heading", "blockquote", "callout", "bulletList", "orderedList", "taskList", "timedList", "codeBlock", "horizontalRule", "table", "image",
        "mathBlock", "diagram", "contactCard", "chart", "overview",
    };

    /// <summary>
    /// Attributes of text blocks that Markdown does not carry and that do not keep a block from being text: the merge gives them back
    /// from the stored node (an unchanged block keeps all of it, a block edited in place these, <c>RichMarkdownMerge</c>). A table's
    /// <c>chartRef</c> is not among them: a chart reads that table, so it stays a placeholder.
    /// </summary>
    internal static readonly Dictionary<string, string[]> Presentation = new(StringComparer.Ordinal)
    {
        ["paragraph"] = ["textAlign"],
        // A heading's former ids (heading-anchors 2) are kept as presentation is.
        ["heading"] = ["textAlign", "aliases"],
        ["orderedList"] = ["type"],
        ["tableRow"] = ["height"],
        ["tableHeader"] = ["backgroundColor", "colwidth"],
        ["tableCell"] = ["backgroundColor", "colwidth"],
        // An overview's id is the editor's; its Markdown (```a-overview) carries the kind.
        ["overview"] = ["id"],
    };

    [GeneratedRegex(@"\G⟦c:(?<index>[1-9][0-9]{0,5})⟧")]
    private static partial Regex PlaceholderAt();

    [GeneratedRegex(@"[\u0000-\u001f\u007f⟦⟧]+")]
    private static partial Regex LabelNoise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static ContentProjection Project(RichDocument document, ProjectionOptions options)
    {
        var items = new List<Item>();
        var number = 0;
        for (var index = 0; index < document.Blocks.Count; index++)
        {
            var block = document.Blocks[index];
            // An empty paragraph has no Markdown form; the merge keeps it with its neighbours.
            if (RichDocument.TypeOf(block) == "paragraph" && block["content"] is not JsonArray { Count: > 0 })
            {
                continue;
            }
            var item = new Item(block, index) { ReadablePageTitle = options.ReadablePageTitle };
            item.Prepared = Prepare(item, ref number);
            items.Add(item);
        }
        SeparateNeighbours(items);
        Number(items);
        var markdown = Verify(items);
        return Build(items, markdown, document.Document, options);
    }

    private sealed class Item(JsonObject block, int documentIndex)
    {
        public JsonObject Block { get; } = block;

        public int DocumentIndex { get; } = documentIndex;

        /// <summary>The title of a page the reader may read (<see cref="ProjectionOptions.ReadablePageTitle"/>), else null.</summary>
        public Func<Guid, string?>? ReadablePageTitle { get; init; }

        public string Path { get; } = "/content/" + documentIndex.ToString(CultureInfo.InvariantCulture);

        /// <summary>The block with its inline components replaced by placeholders, when it is text; null for a placeholder block.</summary>
        public JsonObject? Prepared { get; set; }

        public List<(JsonObject Placeholder, JsonObject Original, string Path)> Inline { get; } = [];

        public int Index { get; set; }

        public string Markdown { get; set; } = "";
    }

    /// <summary>The text form of a block that survives the round trip alone, or null.</summary>
    private static JsonObject? Prepare(Item item, ref int number)
    {
        item.Inline.Clear();
        if (!TopLevelText.Contains(RichDocument.TypeOf(item.Block)))
        {
            return null;
        }
        // An inline formula written as $…$ and an inline contact written as [Jméno](a-contact:…) are text where the block reads back with
        // them, else inline placeholders like any other component.
        foreach (var formulas in Contains(item.Block, "mathInline") || Contains(item.Block, "contactInline") ? [true, false] : new[] { false })
        {
            item.Inline.Clear();
            var prepared = (JsonObject)item.Block.DeepClone();
            if (!Replace(prepared, item.Path, false, formulas, item, ref number))
            {
                break;
            }
            try
            {
                var markdown = Write(prepared);
                if (Guarded(markdown, item.Inline.Count) && Parse(markdown) is [var parsed] && RichDocument.StructurallyEqual(parsed, prepared))
                {
                    return prepared;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException or ApiException)
            {
                // A malformed attribute (a level that is no number) or nesting beyond the parser's limit: the block stays as it is.
            }
        }
        item.Inline.Clear();
        return null;
    }

    /// <summary>Markdown as the merge reads it back: the editor dialect of <see cref="MarkdownToRich.PagePlugins"/>.</summary>
    private static JsonArray Parse(string markdown)
    {
        return MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins).Blocks;
    }

    private static bool Contains(JsonNode? node, string type)
    {
        return RichDocument.TypeOf(node) == type || (node?["content"] as JsonArray ?? []).Any(x => Contains(x, type));
    }

    /// <summary>Replaces the inline components of text blocks by placeholders; false when a component sits at a block position.</summary>
    private static bool Replace(JsonObject node, string path, bool inline, bool formulas, Item item, ref int number)
    {
        var type = RichDocument.TypeOf(node);
        if (inline)
        {
            return type is "text" or "hardBreak" || (formulas && (type == "contactInline" || (type == "mathInline" && EditorMarkdownWriter.WritesInlineMath(node))));
        }
        // A table GFM cannot carry (```a-table) stays a placeholder (dialect §1.2 2026-10-16): the merge pairs table cells by column.
        if (!TextBlocks.Contains(type) || (type == "table" && !MarkdownDialect.HasInlineCells(node)))
        {
            return false;
        }
        if (type == "image")
        {
            return MarkdownImage(node);
        }
        if (Presentation.TryGetValue(type, out var presentation) && node["attrs"] is JsonObject attributes)
        {
            // Compared and written without it; the stored node keeps it.
            foreach (var key in presentation)
            {
                attributes.Remove(key);
            }
        }
        if (node["content"] is not JsonArray content)
        {
            return true;
        }
        var textblock = type is "paragraph" or "heading";
        for (var index = 0; index < content.Count; index++)
        {
            if (content[index] is not JsonObject child)
            {
                return false;
            }
            var childPath = path + "/content/" + index.ToString(CultureInfo.InvariantCulture);
            if (type == "codeBlock")
            {
                if (RichDocument.TypeOf(child) != "text")
                {
                    return false;
                }
                continue;
            }
            // A link of the dependency line is text when the reader may read its page: the line shows the title it stores.
            if (type == "paragraph" && RichDocument.TypeOf(child) == "pageReference" && RequirementHeadingMarkdown.IsDependencyLine(node)
                && Guid.TryParse(Text(child["attrs"]?["id"]), out var page) && item.ReadablePageTitle?.Invoke(page) is not null)
            {
                continue;
            }
            if (textblock && !Replace(child, childPath, true, formulas, item, ref number))
            {
                var placeholder = new JsonObject { ["type"] = MarkdownToRich.SlotNodeType, ["attrs"] = new JsonObject { ["index"] = ++number } };
                if (child["marks"] is JsonArray marks)
                {
                    placeholder["marks"] = marks.DeepClone();
                }
                item.Inline.Add((placeholder, child, childPath));
                content[index] = placeholder;
                continue;
            }
            if (!textblock && !Replace(child, childPath, false, formulas, item, ref number))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// An image of an attachment becomes, in the prepared copy, the node the parser reads from its Markdown; any other image is
    /// no text. The stored node keeps its size and presentation.
    /// </summary>
    private static bool MarkdownImage(JsonObject node)
    {
        var attrs = node["attrs"];
        if (node.Any(x => x.Key is not ("type" or "attrs")) || Text(attrs?["src"]) is not { } src || MarkdownToRich.AttachmentImageSrc(src) != src
            || attrs?["alt"] is not (null or JsonValue) || attrs?["title"] is not (null or JsonValue))
        {
            return false;
        }
        var markdown = MarkdownToRich.ImageNode(src, Text(attrs?["alt"]), Text(attrs?["title"]));
        node["attrs"] = markdown["attrs"]!.DeepClone();
        return true;
    }

    private static string Write(JsonObject block)
    {
        var markdown = EditorMarkdownWriter.WriteProjection(new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(block.DeepClone()) });
        return markdown.TrimEnd('\n');
    }

    /// <summary>
    /// No password directive, and every bracket that a reader would take for a placeholder (an even run of backslashes
    /// before it) is one of the block's own placeholders. Code cannot escape, so code holding a bracket fails here.
    /// </summary>
    private static bool Guarded(string markdown, int placeholders)
    {
        if (markdown.Contains(":secret", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var found = 0;
        for (var index = 0; index < markdown.Length; index++)
        {
            if (markdown[index] is not (MarkdownToRich.PlaceholderOpen or MarkdownToRich.PlaceholderClose))
            {
                continue;
            }
            var backslashes = 0;
            while (index - backslashes > 0 && markdown[index - backslashes - 1] == '\\')
            {
                backslashes++;
            }
            if (backslashes % 2 == 1)
            {
                continue;
            }
            if (markdown[index] == MarkdownToRich.PlaceholderClose || PlaceholderAt().Match(markdown, index) is not { Success: true } match || match.Index != index)
            {
                return false;
            }
            found++;
            index += match.Length - 1;
        }
        return found == placeholders;
    }

    /// <summary>
    /// Text blocks written next to each other must stay apart (two lists of one kind would merge into one): when a pair does
    /// not read back as the same two blocks, the second becomes a placeholder. Blank-line separated blocks interact only
    /// with their neighbour, since the writer escapes every construct that could reach further (link definitions, HTML).
    /// </summary>
    private static void SeparateNeighbours(List<Item> items)
    {
        for (var index = 1; index < items.Count; index++)
        {
            var previous = items[index - 1];
            var current = items[index];
            if (previous.Prepared is null || current.Prepared is null)
            {
                continue;
            }
            var pair = Parse(Write(previous.Prepared) + "\n\n" + Write(current.Prepared));
            if (pair.Count != 2 || !RichDocument.StructurallyEqual(pair[0], previous.Prepared) || !RichDocument.StructurallyEqual(pair[1], current.Prepared))
            {
                current.Prepared = null;
                current.Inline.Clear();
            }
        }
    }

    /// <summary>Placeholders numbered from 1 in document order, block and inline alike; then each item's Markdown.</summary>
    private static void Number(List<Item> items)
    {
        var number = 0;
        foreach (var item in items)
        {
            if (item.Prepared is null)
            {
                item.Index = ++number;
                item.Markdown = MarkdownToRich.Placeholder(item.Index);
                continue;
            }
            foreach (var (placeholder, _, _) in item.Inline)
            {
                placeholder["attrs"]!["index"] = ++number;
            }
            item.Markdown = Write(item.Prepared);
        }
    }

    /// <summary>
    /// The whole text must read back as the items in order. The neighbour check guarantees it; should it ever not hold, the
    /// first block that differs becomes a placeholder and the text is checked again.
    /// </summary>
    private static string Verify(List<Item> items)
    {
        for (var pass = 0; pass <= items.Count; pass++)
        {
            var markdown = items.Count == 0 ? "" : string.Join("\n\n", items.Select(x => x.Markdown)) + "\n";
            var parsed = Parse(markdown);
            var failing = -1;
            for (var index = 0; index < items.Count && failing < 0; index++)
            {
                if (index >= parsed.Count || !Matches(items[index], parsed[index]))
                {
                    failing = index;
                }
            }
            if (failing < 0 && parsed.Count == items.Count)
            {
                return markdown;
            }
            // A placeholder line cannot fail alone; the text block before it took it in.
            failing = failing < 0 ? items.Count - 1 : failing;
            while (failing >= 0 && items[failing].Prepared is null)
            {
                failing--;
            }
            if (failing < 0)
            {
                break;
            }
            items[failing].Prepared = null;
            items[failing].Inline.Clear();
            Number(items);
        }
        throw new InvalidOperationException("The Markdown projection did not stabilize.");
    }

    private static bool Matches(Item item, JsonNode? parsed)
    {
        if (item.Prepared is not null)
        {
            return RichDocument.StructurallyEqual(parsed, item.Prepared);
        }
        return parsed?["content"] is JsonArray { Count: 1 } content && content[0] is JsonObject slot && RichDocument.TypeOf(parsed) == "paragraph"
            && RichDocument.TypeOf(slot) == MarkdownToRich.SlotNodeType && slot["marks"] is null
            && slot["attrs"]?["index"]?.GetValue<int>() == item.Index;
    }

    private static ContentProjection Build(List<Item> items, string markdown, JsonObject document, ProjectionOptions options)
    {
        var components = new List<ComponentSlot>();
        var bindings = new List<SlotBinding>();
        var blocks = new List<ProjectedBlock>();
        var headings = new List<(Item Item, int Level, int Start)>();
        var lines = new List<(int Start, int End)>();
        var occurrences = new Dictionary<Guid, int>();
        var line = 1;
        foreach (var item in items)
        {
            var end = line + item.Markdown.Count(x => x == '\n');
            lines.Add((line, end));
            if (RichDocument.TypeOf(item.Block) == "heading")
            {
                headings.Add((item, item.Block["attrs"]?["level"] is JsonValue level && level.TryGetValue<int>(out var value) ? value : 1, line));
            }
            if (item.Prepared is null)
            {
                components.Add(new ComponentSlot(item.Index, RichDocument.TypeOf(item.Block), Label(item.Block, options), false)
                {
                    Embeds = Embeds(item.Block, occurrences),
                    Text = ReadableText(item.Block, document, options),
                });
                bindings.Add(new SlotBinding(item.Index, item.Block.DeepClone(), item.Path));
            }
            else
            {
                // Images in text are addressed by the id in their Markdown; they count towards the occurrences all the same.
                Embeds(item.Block, occurrences);
                foreach (var (placeholder, original, path) in item.Inline)
                {
                    var index = placeholder["attrs"]!["index"]!.GetValue<int>();
                    components.Add(new ComponentSlot(index, RichDocument.TypeOf(original), Label(original, options), true));
                    bindings.Add(new SlotBinding(index, original.DeepClone(), path));
                }
            }
            blocks.Add(new ProjectedBlock(item.DocumentIndex, (JsonObject?)item.Prepared?.DeepClone(), item.Prepared is null ? item.Index : 0,
                item.Inline.Select(x => x.Placeholder["attrs"]!["index"]!.GetValue<int>()).ToArray()));
            line = end + 2;
        }

        var sections = new List<ContentSection>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < headings.Count; ordinal++)
        {
            var (item, level, start) = headings[ordinal];
            var next = headings.Skip(ordinal + 1).FirstOrDefault(x => x.Level <= level);
            var last = next.Item is null ? items.Count - 1 : items.IndexOf(next.Item) - 1;
            var id = item.Block["attrs"]?["headingId"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            var stable = id is not null && HeadingIds.IsValid(id) && !HeadingIds.IsTemporary(id) && used.Add(id);
            sections.Add(new ContentSection(
                stable ? id! : "v" + options.Version.ToString(CultureInfo.InvariantCulture) + "-h" + (ordinal + 1).ToString(CultureInfo.InvariantCulture),
                !stable, level, HeadingTitle(item.Block), start, lines[last].End));
        }

        var warnings = new List<string>();
        if (markdown.Length > OutputLimit)
        {
            warnings.Add("Stránka má v Markdownu víc než 200 000 znaků; čtěte ji po sekcích.");
        }
        if (sections.Any(x => x.HeadingIdIsTemporary))
        {
            warnings.Add("Některé nadpisy nemají stálé ID; jejich dočasné ID platí jen pro tuto verzi stránky.");
        }
        return new ContentProjection(markdown, sections, components, markdown.Length <= OutputLimit, warnings)
        {
            Bindings = bindings,
            Blocks = blocks,
        };
    }

    /// <summary>
    /// The embeds of a block, numbered per attachment in document order over the whole page (text blocks are counted too): what
    /// atlas_update_embed's occurrence means. Images and file cards are block nodes, so an inline placeholder never holds one.
    /// </summary>
    private static ComponentEmbed[] Embeds(JsonObject block, Dictionary<Guid, int> occurrences)
    {
        return RichMarkdownMerge.EmbedsIn(block).Select(x =>
        {
            var occurrence = occurrences.GetValueOrDefault(x.AttachmentId) + 1;
            occurrences[x.AttachmentId] = occurrence;
            return new ComponentEmbed(x.AttachmentId, x.Kind, occurrence);
        }).ToArray();
    }

    /// <summary>
    /// <see cref="ComponentSlot.Text"/> (review K1): the summary's line, or a block holding requirements as the stored projection writes
    /// it within the whole document, every node Markdown does not carry as text (a component, a remote image) in its label in brackets,
    /// so the text has no password directive, URL of a component or unreadable page title. Null for any other block.
    /// </summary>
    private static string? ReadableText(JsonObject block, JsonObject document, ProjectionOptions options)
    {
        if (RequirementText.IsSummary(block))
        {
            return "**" + RequirementText.SummaryLabel + ":** " + RequirementText.Summary(document);
        }
        if (!Contains(block, "requirement"))
        {
            return null;
        }
        var readable = (JsonObject)block.DeepClone();
        Readable(readable, options);
        return EditorMarkdownWriter.Write(new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(readable) }, document).TrimEnd('\n');
    }

    private static void Readable(JsonObject node, ProjectionOptions options)
    {
        if (RichDocument.TypeOf(node) == "codeBlock" || node["content"] is not JsonArray content)
        {
            return;
        }
        var inline = RichDocument.TypeOf(node) is "paragraph" or "heading";
        for (var index = 0; index < content.Count; index++)
        {
            if (content[index] is not JsonObject child)
            {
                continue;
            }
            var type = RichDocument.TypeOf(child);
            if (inline ? type is "text" or "hardBreak" or "emoji" or "mathInline" : type is not "image" && (type == "requirement" || TextBlocks.Contains(type)))
            {
                Readable(child, options);
                continue;
            }
            var label = new JsonObject { ["type"] = "text", ["text"] = "(" + Label(child, options) + ")" };
            content[index] = inline ? label : new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(label) };
        }
    }

    private static string HeadingTitle(JsonObject heading)
    {
        var builder = new StringBuilder();
        foreach (var child in (heading["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            builder.Append(RichDocument.TypeOf(child) switch
            {
                "text" => child["text"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "",
                "hardBreak" => " ",
                _ => "",
            });
        }
        return Clean(builder.ToString(), 500);
    }

    /// <summary>A readable description without what a reader may not see; inline and block forms share it.</summary>
    private static string Label(JsonObject node, ProjectionOptions options)
    {
        var attrs = node["attrs"];
        var data = attrs?["data"];
        return RichDocument.TypeOf(node) switch
        {
            "secretReference" or "secretInlineReference" => "Odkaz na heslo",
            "contactInline" or "contactCard" => Named("Kontakt", string.Join(", ", (data?["contacts"] as JsonArray ?? []).Select(x => Text(x?["name"])).Where(x => x.Length > 0))),
            "youtrackIssue" => Named("Úkol YouTrack", Text(attrs?["key"])),
            "attachment" => Named("Příloha", Text(data?["name"])),
            "pageReference" => Named("Odkaz na stránku",
                Guid.TryParse(Text(attrs?["id"]), out var page) && options.ReadablePageTitle?.Invoke(page) is { } title ? title : ""),
            "chart" => "Graf",
            "diagram" => "Diagram",
            "openapi" => Named("OpenAPI", EditorComponentText.OpenApi(data as JsonObject)),
            "mathBlock" or "mathInline" => "Vzorec",
            "tableOfContents" => "Obsah dokumentu",
            "image" => Named("Obrázek", Text(attrs?["alt"])),
            "emoji" => Named("Emoji", Text(attrs?["name"])),
            "concealedContent" => "Skrytý obsah",
            "requirement" => Named("Požadavek", RequirementText.Title(node)),
            "overview" or "requirementSummary" => RequirementText.IsSummary(node) ? "Souhrn nasazení" : Named("Přehled", Text(attrs?["kind"])),
            "paragraph" => "Odstavec",
            "heading" => "Nadpis",
            "bulletList" => "Seznam",
            "orderedList" => "Číslovaný seznam",
            "taskList" => "Seznam úkolů",
            "timedList" => "Časová odrážka",
            "blockquote" => "Citace",
            "callout" => "Informační box",
            "codeBlock" => "Blok kódu",
            "table" => "Tabulka",
            "horizontalRule" => "Oddělovač",
            _ => "Komponenta",
        };
    }

    private static string Named(string kind, string name)
    {
        var clean = Clean(name, MaxLabelText);
        return clean.Length == 0 ? kind : kind + ": " + clean;
    }

    private static string Clean(string value, int limit)
    {
        var clean = Spaces().Replace(LabelNoise().Replace(value, " "), " ").Trim();
        return clean.Length <= limit ? clean : clean[..(limit - 1)].TrimEnd() + "…";
    }

    private static string Text(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    }
}
