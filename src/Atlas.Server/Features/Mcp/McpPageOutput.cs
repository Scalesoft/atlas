using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.Mcp;

/// <summary>
/// atlas_get_page as text: the page facts, the section outline and the component list inside <c>&lt;wiki-page-info&gt;</c>, the Markdown
/// inside <c>&lt;wiki-page&gt;</c>. A page (or a section) longer than one result is paged at heading boundaries, else at line
/// boundaries, with a cursor bound to the projected version, the section and the version kind (S0: ≤ 40 000 characters).
/// </summary>
public static partial class McpPageOutput
{
    public const string CursorKind = "page";
    private const int OutlineBudget = 8_000;
    private const int ComponentBudget = 6_000;
    private const int MaxListed = 300;
    /// <summary>The readable text of the components (<see cref="ComponentSlot.Text"/>) one result shows; the body gives way for it.</summary>
    private const int ReadableBudget = 12_000;
    /// <summary>The trust notice, the facts, both wrappers and the paging footer stay well within this.</summary>
    private const int FrameReserve = 3_000;

    private const string ReadableNote = "  Text bloku, jen ke čtení (mění ho nástroje projector_*):\n";
    private const string MissingNote = "  Text bloku se sem nevešel; přečtete ho přes projector_get_requirements.\n";
    /// <summary>What the note under a component with text costs the list: the longer of the two.</summary>
    private static readonly int NoteLength = Math.Max(ReadableNote.Length, MissingNote.Length);

    [GeneratedRegex(@"(?<!\\)⟦c:(?<index>[1-9][0-9]{0,5})⟧")]
    private static partial Regex Placeholder();

    /// <summary>A component's line: its placeholder, label and what atlas_update_embed addresses for each embed inside it (the attachment
    /// id and its occurrence on the page).</summary>
    private static string ListLine(ComponentSlot slot)
    {
        var line = new StringBuilder("- ⟦c:").Append(slot.Index).Append("⟧ ").Append(McpText.Line(slot.Label, 100)).Append(slot.Inline ? " (v textu)" : "");
        foreach (var embed in slot.Embeds.Take(MaxListed))
        {
            line.Append(embed.Kind == EmbedKind.Image ? " · obrázek " : " · soubor ").Append(embed.AttachmentId)
                .Append(" (occurrence ").Append(embed.Occurrence.ToString(CultureInfo.InvariantCulture)).Append(')');
        }
        return line.Append('\n').ToString();
    }

    public static CallToolResult Render(McpPageRead read, ContentProjection projection, string? headingId, string? cursor)
    {
        var page = read.Page;
        var markdown = projection.Markdown;
        var lineStarts = LineStarts(markdown);
        var (rangeStart, rangeEnd, section) = Range(projection, markdown, lineStarts, headingId);
        var start = Start(read, headingId, cursor, rangeStart, rangeEnd);

        var info = Info(read, projection);
        // The body gives way for the text (with its indent and heading line) of the components it can reach, up to the budget the
        // list shows; components further down the page take nothing from this window (S2-2).
        var reach = markdown[start..Math.Min(rangeEnd, start + Math.Max(McpText.Limit - FrameReserve - info.Length, 1_000))];
        var reachable = Placeholder().Matches(reach).Select(match => int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture)).ToHashSet();
        var reachableSlots = projection.Components.Where(slot => reachable.Contains(slot.Index)).ToArray();
        var readable = Math.Min(ReadableBudget, reachableSlots.Where(slot => slot.Text is not null)
            .Sum(slot => slot.Text!.Length + 4 * slot.Text.Count(x => x == '\n')));
        // The component lines and the note under each come out of their own budget (S3-1).
        var listing = Math.Min(ComponentBudget, reachableSlots.Sum(slot => ListLine(slot).Length + (slot.Text is null ? 0 : NoteLength)));
        var budget = McpText.Limit - FrameReserve - info.Length - readable - listing;
        var end = End(projection, markdown, lineStarts, start, rangeEnd, Math.Max(budget, 1_000));
        var body = markdown[start..end];
        var truncated = end < rangeEnd;
        var nextCursor = truncated
            ? McpCursor.Create(CursorKind, new JsonObject { ["v"] = read.ProjectedVersion, ["w"] = read.Working, ["h"] = headingId, ["o"] = end })
            : null;
        var shown = Placeholder().Matches(body).Select(match => int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture)).ToHashSet();
        var components = projection.Components.Where(slot => shown.Contains(slot.Index)).ToArray();
        var warnings = projection.Warnings.ToList();
        // A working read needs EditContent, so a read-only copy there means password references the actor may not see (PageReader).
        if (!page.CanEdit && read.Working)
        {
            warnings.Add("Stránka obsahuje obsah, ke kterému nemáte přístup; přes MCP ji nelze upravit.");
        }

        var text = new StringBuilder();
        // The components and the text of them the result lists; the structured content carries the same, no more (S2-2, S3-1).
        var listed = new List<ComponentSlot>();
        var readableShown = new HashSet<int>();
        text.Append(McpText.TrustNotice).Append("\n\n");
        text.Append(read.Working ? "Pracovní verze stránky wiki" : "Publikovaná verze stránky wiki");
        text.Append(" (verze ").Append(read.ProjectedVersion.ToString(CultureInfo.InvariantCulture)).Append(").\n");
        text.Append("- pageId: ").Append(page.Id).Append('\n');
        text.Append("- spaceId: ").Append(page.SpaceId).Append('\n');
        text.Append("- publikovaná: ").Append(page.IsPublished ? "ano" : "ne").Append('\n');
        if (page.Verification is { } verification && PageVerifications.PageVerificationText.Line(verification) is { } verified)
        {
            text.Append("- ověření: ").Append(verified).Append('\n');
        }
        if (page.CanEdit)
        {
            text.Append("- expectedVersion (pro zápis): ").Append(page.Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (!read.Working && page.PublishedVersion != page.Version)
            {
                text.Append("- pracovní verze je novější než publikovaná; přečtete ji s version: \"working\"\n");
            }
        }
        text.Append("- markdownEditable: ").Append(projection.MarkdownEditable ? "ano" : "ne").Append('\n');
        if (section is not null)
        {
            text.Append("- sekce: headingId ").Append(section.HeadingId).Append(" (včetně podsekcí)\n");
        }
        if (truncated || start > rangeStart)
        {
            text.Append("- zobrazeno: znaky ").Append(start - rangeStart).Append('–').Append(end - rangeStart).Append(" z ").Append(rangeEnd - rangeStart).Append('\n');
        }
        text.Append("Komponenty jsou zástupné značky ⟦c:N⟧; při úpravách je vraťte beze změny.\n\n");
        text.Append("<wiki-page-info>\n").Append(info).Append("</wiki-page-info>\n\n");
        text.Append("<wiki-page id=\"").Append(page.Id).Append("\" version=\"").Append(read.Working ? "working" : "published").Append("\">\n");
        text.Append(McpText.Content(body).TrimEnd('\n')).Append("\n</wiki-page>\n");
        if (components.Length > 0)
        {
            text.Append("\nKomponenty v tomto výřezu:\n<wiki-page-components>\n");
            var readableLeft = ReadableBudget;
            var listLeft = ComponentBudget;
            foreach (var slot in components)
            {
                var line = ListLine(slot);
                if (line.Length + (slot.Text is null ? 0 : NoteLength) > listLeft)
                {
                    break;
                }
                listLeft -= line.Length + (slot.Text is null ? 0 : NoteLength);
                listed.Add(slot);
                text.Append(line);
                // Review K1: what the block says, beside its placeholder; the placeholder line alone goes back into an edit.
                if (slot.Text is { } blockText)
                {
                    var indented = "    " + McpText.Content(blockText).Replace("\n", "\n    ", StringComparison.Ordinal);
                    if (indented.Length <= readableLeft)
                    {
                        readableLeft -= indented.Length;
                        readableShown.Add(slot.Index);
                        text.Append(ReadableNote).Append(indented).Append('\n');
                    }
                    else
                    {
                        text.Append(MissingNote);
                    }
                }
            }
            if (listed.Count < components.Length)
            {
                text.Append("- … a dalších ").Append(components.Length - listed.Count).Append(" komponent se sem nevešlo")
                    .Append(components.Skip(listed.Count).Any(slot => slot.Text is not null) ? "; bloky požadavků vrací projector_get_requirements" : "")
                    .Append(", nebo čtěte po sekcích s headingId.\n");
            }
            text.Append("</wiki-page-components>\n");
        }
        foreach (var warning in warnings.Where(x => x.Length > 0))
        {
            text.Append("\nUpozornění: ").Append(McpText.Line(warning, 300)).Append('\n');
        }
        if (nextCursor is not null)
        {
            text.Append("\nStránka pokračuje. Další část přečtete se stejnými parametry a cursor: \"").Append(nextCursor)
                .Append("\", nebo čtěte po sekcích s headingId.\n");
        }

        var structured = new JsonObject
        {
            ["pageId"] = page.Id,
            ["title"] = page.Title,
            ["spaceId"] = page.SpaceId,
            ["path"] = read.Path,
            ["version"] = read.Working ? "working" : "published",
            ["revisionVersion"] = read.ProjectedVersion,
            ["isPublished"] = page.IsPublished,
            ["markdownEditable"] = projection.MarkdownEditable,
            ["truncated"] = truncated,
            ["sections"] = new JsonArray(projection.Sections.Take(MaxListed).Select(x => (JsonNode)new JsonObject
            {
                ["headingId"] = x.HeadingId, ["temporary"] = x.HeadingIdIsTemporary, ["level"] = x.Level, ["title"] = x.Title
            }).ToArray()),
            ["components"] = new JsonArray(listed.Select(x => (JsonNode)new JsonObject
            {
                ["index"] = x.Index, ["type"] = x.Type, ["label"] = McpText.Line(x.Label, 100), ["inline"] = x.Inline, ["text"] = readableShown.Contains(x.Index) ? x.Text : null,
                ["embeds"] = new JsonArray(x.Embeds.Take(MaxListed).Select(e => (JsonNode)new JsonObject
                {
                    ["attachmentId"] = e.AttachmentId, ["kind"] = e.Kind == EmbedKind.Image ? "image" : "file", ["occurrence"] = e.Occurrence
                }).ToArray())
            }).ToArray()),
            ["warnings"] = new JsonArray(warnings.Where(x => x.Length > 0).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray())
        };
        if (page.CanEdit)
        {
            structured["expectedVersion"] = page.Version;
        }
        if (page.Verification is { } block)
        {
            structured["verification"] = PageVerifications.PageVerificationText.Json(block);
        }
        if (section is not null)
        {
            structured["headingId"] = section.HeadingId;
        }
        if (nextCursor is not null)
        {
            structured["nextCursor"] = nextCursor;
        }
        return McpText.Result(text.ToString(), structured);
    }

    private static string Info(McpPageRead read, ContentProjection projection)
    {
        var info = new StringBuilder();
        info.Append("Název: ").Append(McpText.Line(read.Page.Title, 300)).Append('\n');
        info.Append("Cesta: ").Append(McpText.Line(read.Path, 600)).Append('\n');
        if (projection.Sections.Count > 0)
        {
            info.Append("Sekce (headingId):\n");
            var listed = 0;
            foreach (var section in projection.Sections)
            {
                var line = new string(' ', 2 * Math.Clamp(section.Level - 1, 0, 5)) + "- " + McpText.Line(section.Title, 150) + " (" + section.HeadingId
                    + (section.HeadingIdIsTemporary ? ", dočasné" : "") + ")\n";
                if (info.Length + line.Length > OutlineBudget || listed >= MaxListed)
                {
                    info.Append("- … a dalších ").Append(projection.Sections.Count - listed).Append(" sekcí (structuredContent.sections)\n");
                    break;
                }
                info.Append(line);
                listed++;
            }
        }
        if (projection.Components.Count > 0)
        {
            info.Append("Komponent na stránce: ").Append(projection.Components.Count).Append('\n');
        }
        return info.Length > OutlineBudget + ComponentBudget ? info.ToString()[..McpText.CutAt(info.ToString(), OutlineBudget + ComponentBudget)] + "\n" : info.ToString();
    }

    private static int[] LineStarts(string markdown)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < markdown.Length; index++)
        {
            if (markdown[index] == '\n' && index + 1 < markdown.Length)
            {
                starts.Add(index + 1);
            }
        }
        return starts.ToArray();
    }

    private static int LineOffset(int[] lineStarts, int line, int length) => line - 1 < lineStarts.Length ? lineStarts[Math.Max(line - 1, 0)] : length;

    private static (int Start, int End, ContentSection? Section) Range(ContentProjection projection, string markdown, int[] lineStarts, string? headingId)
    {
        if (string.IsNullOrEmpty(headingId))
        {
            return (0, markdown.Length, null);
        }
        var section = projection.Sections.FirstOrDefault(x => string.Equals(x.HeadingId, headingId, StringComparison.Ordinal))
            ?? throw new ApiException(404, "Sekce s tímto headingId na stránce není. Seznam sekcí vrací atlas_get_page bez headingId.", "heading_not_found");
        var start = LineOffset(lineStarts, section.StartLine, markdown.Length);
        var end = section.EndLine < lineStarts.Length ? lineStarts[section.EndLine] : markdown.Length;
        return (start, Math.Max(start, end), section);
    }

    private static int Start(McpPageRead read, string? headingId, string? cursor, int rangeStart, int rangeEnd)
    {
        var position = McpCursor.Read(cursor, CursorKind);
        if (position is null)
        {
            return rangeStart;
        }
        if (position["w"]?.GetValue<bool>() != read.Working || (string?)position["h"] != (string.IsNullOrEmpty(headingId) ? null : headingId))
        {
            throw new ApiException(400, McpCursor.Invalid, "invalid_argument");
        }
        if (McpCursor.Int(position, "v", 0, int.MaxValue) != read.ProjectedVersion)
        {
            throw new ApiException(409, "Stránka se mezitím změnila. Načtěte ji znovu bez cursor.", "version_conflict");
        }
        var offset = McpCursor.Int(position, "o", 0, int.MaxValue);
        if (offset <= rangeStart || offset >= rangeEnd)
        {
            throw new ApiException(400, McpCursor.Invalid, "invalid_argument");
        }
        return offset;
    }

    /// <summary>The last heading start that fits, else the last line start, else a hard cut; always past <paramref name="start"/>.</summary>
    private static int End(ContentProjection projection, string markdown, int[] lineStarts, int start, int rangeEnd, int budget)
    {
        var limit = start + budget;
        if (rangeEnd <= limit)
        {
            return rangeEnd;
        }
        var heading = projection.Sections.Select(x => LineOffset(lineStarts, x.StartLine, markdown.Length))
            .Where(offset => offset > start && offset <= limit).DefaultIfEmpty(-1).Max();
        // A heading cut only when it keeps at least a quarter of the budget; otherwise a line boundary fills the result better.
        if (heading > start + budget / 4)
        {
            return heading;
        }
        var line = lineStarts.Where(offset => offset > start && offset <= limit).DefaultIfEmpty(-1).Max();
        return line > start ? line : McpText.CutAt(markdown, limit);
    }
}
