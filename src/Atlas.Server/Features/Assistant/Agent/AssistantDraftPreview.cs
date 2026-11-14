using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Server.Features.Assistant.Commands;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Domain;
using Atlas.Server.Features.Mcp.Queries;
using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Queries;
using Atlas.Server.Platform.Content;
using Wolverine;

namespace Atlas.Server.Features.Assistant.Agent;

/// <summary>
/// The server-built preview of a draft write (spec §7.2, §9.2, WP-31): one call of <c>atlas_create_draft</c>,
/// <c>atlas_update_section</c>, <c>atlas_insert_section</c>, <c>atlas_replace_page</c> or <c>atlas_rename_page</c> is one item.
/// It reads under the user's rights what the tool itself reads (the working revision through <see cref="GetMcpEditBase"/>: EditContent,
/// G5, the expected version; the create's checks through <see cref="PrepareMcpDraft"/>) and converts the Markdown without saving, so
/// what the model can fix (a stale version, a missing heading, malformed Markdown, a foreign placeholder) is a tool error within the
/// turn. Only <c>components_removed</c> passes: spec §7.2 lets that attempt fail with 409 on the card, nothing saved.
/// <para>
/// The text shows only the changed sections of the working version's Markdown projection before and after, placeholders as
/// <c>⟦komponenta: …⟧</c>; a new draft shows its place and first lines, a rename the old and new title. Nothing here saves or
/// reaches the model: the user who sees it may edit the page.
/// </para>
/// </summary>
internal static partial class AssistantDraftPreview
{
    private const int MaxText = 20_000;
    private const int FirstLines = 12;
    private const string RemovedCode = "components_removed";

    [GeneratedRegex(@"⟦c:([1-9][0-9]{0,5})⟧")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^#{1,6}(?:\s|$)")]
    private static partial Regex HeadingLine();

    public static Task<AiProposalItemView> ItemAsync(IServiceProvider services, AiProposalShape shape, JsonObject arguments, CancellationToken ct)
    {
        var bus = services.GetRequiredService<IMessageBus>();
        var headingIds = services.GetRequiredService<McpHeadingIds>();
        return shape.Kind switch
        {
            AiProposalShape.NewDraft when shape.Tool == "projector_create_deployment" => DeploymentAsync(bus, arguments, ct),
            AiProposalShape.NewDraft => NewDraftAsync(bus, headingIds, arguments, ct),
            AiProposalShape.Rename => RenameAsync(bus, arguments, ct),
            _ => ContentAsync(bus, headingIds, shape.Tool, arguments, ct),
        };
    }

    private static async Task<AiProposalItemView> ContentAsync(IMessageBus bus, McpHeadingIds headingIds, string tool, JsonObject arguments, CancellationToken ct)
    {
        var pageId = McpArguments.RequiredId(AssistantProposals.Text(arguments["pageId"]), "pageId");
        var version = RequireVersion(arguments);
        var markdown = AssistantProposals.Text(arguments["markdown"]) ?? "";
        var heading = AssistantProposals.Text(arguments["heading"]) ?? "";
        string?[] inputs = tool == "atlas_insert_section" ? [heading, markdown] : [markdown];
        foreach (var input in inputs)
        {
            PageMarkdownSaveRunner.RequireMarkdown(input);
        }
        var edit = await bus.InvokeAsync<McpEditBase>(new GetMcpEditBase(pageId, version, MarkdownLinks.Collect(inputs)), ct);
        var document = RichDocument.Parse(edit.ContentJson);
        var projection = RichMarkdownProjection.Project(document, new ProjectionOptions(edit.Version));
        var context = edit.Links.Context(headingIds.Next);
        var reset = arguments["resetFormatting"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on;
        var lines = projection.Markdown.Split('\n').ToList();
        string after;
        switch (tool)
        {
            case "atlas_update_section":
            {
                var headingId = AssistantProposals.Text(arguments["headingId"]) ?? throw Invalid("headingId je povinné (headingId ze seznamu sekcí atlas_get_page).");
                RichMarkdownMerge.ReplaceSection(document, projection, headingId, markdown, context, reset);
                var section = projection.Sections.First(s => s.HeadingId == headingId);
                after = Splice(lines, section.StartLine - 1, section.EndLine, markdown);
                break;
            }
            case "atlas_insert_section":
            {
                var level = Int(arguments["level"]) is { } value and >= 1 and <= 6 ? value : throw Invalid("level musí být 1 až 6.");
                var afterHeadingId = AssistantProposals.Text(arguments["afterHeadingId"]);
                var where = (afterHeadingId, AssistantProposals.Text(arguments["position"])) switch
                {
                    (not null, null or "after") => SectionPosition.AfterSection,
                    (null, "start") => SectionPosition.Start,
                    (null, null or "end") => SectionPosition.End,
                    _ => throw Invalid("Zadejte buď afterHeadingId, nebo position \"start\" či \"end\"."),
                };
                RichMarkdownMerge.InsertSection(document, projection, afterHeadingId, where, level, heading, markdown, context);
                var at = where switch
                {
                    SectionPosition.Start => 0,
                    SectionPosition.End => lines.Count,
                    _ => projection.Sections.First(s => s.HeadingId == afterHeadingId).EndLine,
                };
                after = Splice(lines, at, at, new string('#', level) + " " + heading.Trim() + "\n\n" + markdown + "\n");
                break;
            }
            default:
            {
                var removals = arguments["removeComponents"] is JsonArray listed ? listed.Select(Int).OfType<int>().ToArray() : [];
                PageMarkdownSaveRunner.RequireRemovals(removals);
                try
                {
                    RichMarkdownMerge.ReplaceAll(document, projection, markdown, context, removals, reset);
                }
                catch (ApiException error) when (error.Code == RemovedCode)
                {
                    // Spec §7.2: a dropped placeholder is the attempt's 409 on the card, nothing saved; the preview shows it gone.
                }
                after = markdown;
                break;
            }
        }
        var text = Changed(projection.Markdown, after, projection.Components);
        if (text.Sections.Count == 0)
        {
            throw Invalid("Návrh text stránky nemění.");
        }
        var page = (await bus.InvokeAsync<AiProposalPages>(new PreviewAiProposalPages([new(pageId, edit.SpaceId)]), ct)).Items.GetValueOrDefault(pageId);
        return new AiProposalItemView(0, pageId, page?.Title ?? edit.Title, page?.Path ?? "", AiProposalShape.Content, null, null, Text: text);
    }

    private static async Task<AiProposalItemView> RenameAsync(IMessageBus bus, JsonObject arguments, CancellationToken ct)
    {
        var pageId = McpArguments.RequiredId(AssistantProposals.Text(arguments["pageId"]), "pageId");
        var version = RequireVersion(arguments);
        var title = RequireTitle(arguments);
        var edit = await bus.InvokeAsync<McpEditBase>(new GetMcpEditBase(pageId, version, MarkdownLinks.Collect()), ct);
        if (title == edit.Title)
        {
            throw Invalid("Stránka už tento název má.");
        }
        var page = (await bus.InvokeAsync<AiProposalPages>(new PreviewAiProposalPages([new(pageId, edit.SpaceId)]), ct)).Items.GetValueOrDefault(pageId);
        return new AiProposalItemView(0, pageId, page?.Title ?? edit.Title, page?.Path ?? "", AiProposalShape.Rename, null, null,
            Text: new AiProposalText([], edit.Title, title));
    }

    private static async Task<AiProposalItemView> NewDraftAsync(IMessageBus bus, McpHeadingIds headingIds, JsonObject arguments, CancellationToken ct)
    {
        var space = McpArguments.RequiredId(AssistantProposals.Text(arguments["spaceId"]), "spaceId");
        var parent = McpArguments.OptionalId(AssistantProposals.Text(arguments["parentId"]), "parentId");
        var template = McpArguments.OptionalId(AssistantProposals.Text(arguments["templatePageId"]), "templatePageId");
        var title = RequireTitle(arguments);
        var markdown = AssistantProposals.Text(arguments["markdown"]) ?? "";
        PageMarkdownSaveRunner.RequireMarkdown(markdown);
        var request = new CreatePageRequest(space, parent, title, "", "", TemplatePageId: template);
        var prepared = await bus.InvokeAsync<PageMarkdownCreateBase>(new PrepareMcpDraft(request, MarkdownLinks.Collect(markdown)), ct);
        PageMarkdownCreate.Convert(request, markdown, prepared.Template, prepared.Links.Context(headingIds.Next));
        var pages = await bus.InvokeAsync<AiProposalPages>(new PreviewAiProposalPages([new(parent ?? Guid.Empty, space)]), ct);
        var where = parent is { } id && pages.Items.TryGetValue(id, out var above) ? above.Path : pages.Spaces.GetValueOrDefault(space) ?? "";
        var chunks = Chunks(markdown);
        var first = string.Join('\n', markdown.Trim().Split('\n').Take(FirstLines));
        return new AiProposalItemView(0, Guid.Empty, title, where + " › " + title, AiProposalShape.NewDraft, null, null,
            Text: new AiProposalText(chunks.Where(c => HeadingLine().IsMatch(c)).Select(SectionTitle).ToArray(), "", Show(first, [])));
    }

    /// <summary>A new deployment as a copy of another (plan 3.5 C, „založ projekt podle Dubovic a přidej telekonference“): where it goes,
    /// the page it copies and the modules it adds, checked as the tool checks them (the source readable, the modules in the space's
    /// catalog), so a wrong id or module is a tool error the model fixes in the turn. Nothing is created.</summary>
    private static async Task<AiProposalItemView> DeploymentAsync(IMessageBus bus, JsonObject arguments, CancellationToken ct)
    {
        var source = McpArguments.RequiredId(AssistantProposals.Text(arguments["sourcePageId"]), "sourcePageId");
        var space = McpArguments.RequiredId(AssistantProposals.Text(arguments["spaceId"]), "spaceId");
        var parent = McpArguments.OptionalId(AssistantProposals.Text(arguments["parentId"]), "parentId");
        var title = RequireTitle(arguments);
        var modules = arguments["moduleKeys"] is JsonArray keys ? keys.Select(AssistantProposals.Text).OfType<string>().Distinct().ToArray() : [];
        // G5 before the source or the target space's catalog is read: one of a space excluded from AI reads as missing.
        var scope = await bus.InvokeAsync<McpScope>(new CheckMcpScope([source], [space]), ct);
        if (!scope.PagesInScope.Contains(source) || scope.ExcludedSpaces.Contains(space))
        {
            throw new ApiException(404, scope.ExcludedSpaces.Contains(space) ? McpVisibility.MissingSpace : McpVisibility.MissingPage);
        }
        var pages =await bus.InvokeAsync<AiProposalPages>(new PreviewAiProposalPages([new(source, space), new(parent ?? Guid.Empty, space)]), ct);
        var copied = pages.Items.TryGetValue(source, out var from) ? from.Title
            : throw Invalid("Zdrojovou stránku nejde přečíst; zakázku podle názvu najde atlas_search nebo projector_overview.");
        var catalog = modules.Length == 0 ? null : await bus.InvokeAsync<ProjectorCatalog>(new GetProjectorCatalog(space), ct);
        var added = modules.Select(module => catalog!.Definitions.Where(definition => definition.ModuleKey == module).ToArray() is [var first, ..] all
            ? $"- {first.Bundle} ({all.Length})"
            : throw Invalid($"Modul „{module}“ v katalogu prostoru není; najde ho projector_get_catalog s query.")).ToArray();
        var where = parent is { } id && pages.Items.TryGetValue(id, out var above) ? above.Path : pages.Spaces.GetValueOrDefault(space) ?? "";
        var text = $"Kopie stránky „{copied}“ bez stavů, termínů, stran, historie a důvodů blokace."
            + (added.Length > 0 ? "\n\nModuly navíc, i s tím, na čem závisí:\n" + string.Join('\n', added) : "");
        return new AiProposalItemView(0, Guid.Empty, title, where + " › " + title, AiProposalShape.NewDraft, null, null, Text: new AiProposalText([], "", text));
    }

    /// <summary>The changed sections: both texts cut at their headings, equal sections at the start and end dropped.</summary>
    internal static AiProposalText Changed(string before, string after, IReadOnlyList<ComponentSlot> components)
    {
        var old = Chunks(before);
        var next = Chunks(after);
        var head = 0;
        while (head < old.Count && head < next.Count && old[head] == next[head])
        {
            head++;
        }
        var tail = 0;
        while (tail < old.Count - head && tail < next.Count - head && old[^(tail + 1)] == next[^(tail + 1)])
        {
            tail++;
        }
        var removed = old.GetRange(head, old.Count - head - tail);
        var added = next.GetRange(head, next.Count - head - tail);
        return new AiProposalText(added.Concat(removed).Select(SectionTitle).Distinct().ToArray(),
            Show(string.Join("\n\n", removed), components), Show(string.Join("\n\n", added), components));
    }

    /// <summary>Markdown cut before each heading line outside a code fence; every part trimmed, empty parts left out.</summary>
    private static List<string> Chunks(string markdown)
    {
        var chunks = new List<string>();
        var current = new List<string>();
        var fenced = false;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }
            else if (!fenced && HeadingLine().IsMatch(line))
            {
                Flush();
            }
            current.Add(line);
        }
        Flush();
        return chunks;

        void Flush()
        {
            var text = string.Join('\n', current).Trim();
            if (text.Length > 0)
            {
                chunks.Add(text);
            }
            current.Clear();
        }
    }

    private static string SectionTitle(string chunk)
        => HeadingLine().IsMatch(chunk) ? chunk.Split('\n')[0].TrimStart('#').Trim() is { Length: > 0 } title ? title : "Nadpis" : "Začátek stránky";

    /// <summary>Placeholders as their readable label (never the node), cut at <see cref="MaxText"/>.</summary>
    private static string Show(string text, IReadOnlyList<ComponentSlot> components)
    {
        var labels = components.ToDictionary(slot => slot.Index, slot => slot.Label);
        text = Placeholder().Replace(text, match => "⟦komponenta: " + labels.GetValueOrDefault(int.Parse(match.Groups[1].Value), "neznámá") + "⟧");
        if (text.Length <= MaxText)
        {
            return text;
        }
        var keep = char.IsHighSurrogate(text[MaxText - 1]) ? MaxText - 1 : MaxText;
        return text[..keep] + "\n…";
    }

    private static string Splice(List<string> lines, int start, int end, string inserted)
        => string.Join('\n', lines.Take(start).Concat(inserted.ReplaceLineEndings("\n").Split('\n')).Concat(lines.Skip(end)));

    private static int RequireVersion(JsonObject arguments)
        => Int(arguments["expectedVersion"]) is { } version and >= 1 ? version : throw Invalid("expectedVersion je povinné: verze z posledního čtení stránky.");

    private static string RequireTitle(JsonObject arguments)
    {
        var title = AssistantProposals.Text(arguments["title"])?.Trim() ?? "";
        return title.Length is 0 or > 200 || title.Any(char.IsControl) ? throw Invalid("Název stránky je povinný, na jednom řádku a nejvýše 200 znaků.") : title;
    }

    private static int? Int(JsonNode? node)
        => node is JsonValue value && (value.TryGetValue<int>(out var number) || value.TryGetValue<string>(out var text) && int.TryParse(text, out number)) ? number : null;

    private static ApiException Invalid(string message) => new(400, message, "invalid_argument");
}
