using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Queries;
using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.PageTags.Queries;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.Search.Contracts;

namespace Atlas.Server.Features.Mcp.Tools;

/// <summary>
/// The labels of a page over MCP: its icon with colour (the page decoration) and its tags. They are properties of the page, not of
/// its text: a change is visible to readers at once, is not published and creates no document revision, so these write tools need
/// the write scope, not publish. Each runs the shared handler of the web and REST write behind G5 (an excluded space or page is
/// the 404 of a missing one), and tag suggestions and listings are filtered by G5 in SQL, so a tag or page that lives only in an
/// excluded space is never revealed. Not a Wolverine handler: the class name does not end with "Handler".
/// </summary>
[McpServerToolType]
public sealed class McpLabelTools
{
    private const string Immediate = "Změna je čtenářům vidět hned, nepublikuje se a nevytváří novou revizi dokumentu (text stránky se nemění).";

    /// <summary>The most changes one atlas_apply_labels call takes (spec §5.2).</summary>
    public const int MaxChanges = McpPageMove.MaxMoves;

    private readonly IMessageBus m_bus;
    private readonly ILogger<McpLabelTools> m_logger;

    public McpLabelTools(IMessageBus bus, ILogger<McpLabelTools> logger)
    {
        m_bus = bus;
        m_logger = logger;
    }

    [McpServerTool(Name = "atlas_get_labels", Title = "Ikona a tagy stránky", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Ikona, barva a tagy jedné stránky (s revisionId a id tagů). pageId.")]
    [Description("Vrátí ikonu a barvu stránky (revisionId pro expectedRevisionId při změně) a její tagy (id pro expectedTagIds). " + McpText.TrustNotice)]
    public Task<CallToolResult> GetLabelsAsync(
        [Description("pageId stránky.")] string pageId,
        CancellationToken ct = default)
        => RunAsync("atlas_get_labels", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            var labels = await m_bus.InvokeAsync<McpLabels>(new GetMcpLabels(id), ct);
            var body = new StringBuilder();
            body.Append("ikona: ").Append(labels.Decoration is { Icon: { } icon, Color: { } color } ? $"{icon}, barva {color}" : "žádná").Append('\n');
            body.Append("expectedRevisionId (pro atlas_set_decoration): ").Append(labels.Decoration?.RevisionId.ToString() ?? "(vynechte, stránka ikonu nemá; nejdřív atlas_list_appearance s pageId nabídne vhodné ikony)").Append('\n');
            body.Append("tagy: ").Append(labels.Tags.Count == 0 ? "žádné" : string.Join(", ", labels.Tags.Select(x => McpText.Line(x.Name, 60)))).Append('\n');
            var structured = new JsonObject
            {
                ["pageId"] = labels.PageId,
                ["spaceId"] = labels.SpaceId,
                ["decoration"] = DecorationNode(labels.Decoration),
                ["tags"] = TagNodes(labels.Tags)
            };
            return McpText.Result(McpText.Wrap($"Ikona a tagy stránky {id}.", body.ToString()), structured);
        }, ct);

    [McpServerTool(Name = "atlas_list_appearance", Title = "Katalog ikon a barev", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Katalog ikon a barev. S pageId vybere vhodnou ikonu: vrátí 5–8 kandidátů (ikona, barva, důvod) podle názvu a textu stránky. Jinak query = část id (např. \"book\"); barvy jsou odstín-tón, např. blue-3.")]
    [Description("S pageId (VYBER VHODNOU IKONU) vrátí 5–8 kandidátů ikon seřazených podle názvu a textu stránky, česky i anglicky, každý s barvou a krátkým důvodem; " +
        "vyberte nejvhodnější a zavolejte atlas_set_decoration. Bez pageId vypíše identifikátory ikon a barev, které stránka smí mít " +
        "(atlas_set_decoration je přijme jen přesně takto). Ikon je přes tisíc: hledejte podle části id (query, např. \"book\" nebo \"folder\"). " +
        "Barvy jsou paleta odstín-tón (např. blue-3; tón 1 nejsvětlejší, 5 nejtmavší). Například ikona book-open a barva blue-3.")]
    public Task<CallToolResult> ListAppearanceAsync(
        [Description("Volitelně pageId stránky, pro kterou se mají navrhnout ikony (kandidáti navíc k výpisu).")] string? pageId = null,
        [Description("Část id ikony (volitelné).")] string? query = null,
        [Description("Počet ikon 1–200, výchozí 100.")] int limit = 100,
        [Description("Kolik ikon přeskočit (stránkování), výchozí 0.")] int offset = 0,
        CancellationToken ct = default)
        => RunAsync("atlas_list_appearance", async () =>
        {
            if (limit is < 1 or > 200)
            {
                return McpErrors.InvalidArgument("limit musí být 1 až 200.");
            }
            var catalog = AppearanceCatalogView.Read(query, limit, offset);
            var body = new StringBuilder();
            IReadOnlyList<IconCandidate>? candidates = null;
            if (!string.IsNullOrEmpty(pageId))
            {
                var id = McpArguments.RequiredId(pageId, "pageId");
                try
                {
                    var read = await m_bus.InvokeAsync<McpPageRead>(new GetMcpPage(id, Working: false, RecordOpening: false), ct);
                    candidates = IconSuggester.Suggest(read.Page.Title, IconSuggester.TextOf(read.Page.ContentJson));
                }
                catch (ApiException error) when (error.Code == "not_published")
                {
                    // A never published draft by its title only (the tree shows it): its text is working text, which the assistant
                    // may read only through its gate (AssistantToolSession.WorkingTextRefusalAsync).
                    var read = await m_bus.InvokeAsync<McpPageRead>(new GetMcpPage(id, Working: true, RecordOpening: false), ct);
                    candidates = IconSuggester.Suggest(read.Page.Title, null);
                }
                body.Append("Navržené ikony pro stránku ").Append(id).Append(" (nejlepší první; vyberte a zavolejte atlas_set_decoration):\n");
                for (var i = 0; i < candidates.Count; i++)
                {
                    body.Append(i + 1).Append(". ").Append(candidates[i].Icon).Append(", barva ").Append(candidates[i].Color).Append(" (").Append(McpText.Line(candidates[i].Reason, 100)).Append(")\n");
                }
            }
            // With candidates and no query the catalog listing is only noise (the full list stays one call away without pageId).
            if (candidates is null || !string.IsNullOrEmpty(query))
            {
                body.Append("ikony (").Append(catalog.Icons.Count).Append(" z ").Append(catalog.IconTotal).Append("): ").Append(string.Join(", ", catalog.Icons)).Append('\n');
            }
            body.Append("barvy: ").Append(string.Join(", ", catalog.Colors.Select(x => x.Id))).Append('\n');
            body.Append("starší barvy (stále platné): ").Append(string.Join(", ", catalog.LegacyColors)).Append('\n');
            var structured = new JsonObject
            {
                ["icons"] = new JsonArray(catalog.Icons.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["iconTotal"] = catalog.IconTotal,
                ["colors"] = new JsonArray(catalog.Colors.Select(x => (JsonNode)new JsonObject { ["id"] = x.Id, ["hue"] = x.Hue, ["tone"] = x.Tone }).ToArray()),
                ["legacyColors"] = new JsonArray(catalog.LegacyColors.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray())
            };
            if (offset + catalog.Icons.Count < catalog.IconTotal)
            {
                body.Append("Další ikony: zavolejte znovu s offset: ").Append(offset + catalog.Icons.Count).Append(".\n");
                structured["nextOffset"] = offset + catalog.Icons.Count;
            }
            if (candidates is not null)
            {
                structured["candidates"] = new JsonArray(candidates.Select(x => (JsonNode)new JsonObject { ["icon"] = x.Icon, ["color"] = x.Color, ["reason"] = x.Reason }).ToArray());
            }
            // The identifiers are the server's own constants and a reason quotes only a stem of the table or a part of an icon id: no page text, no trust wrapper.
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = body.ToString() }],
                StructuredContent = JsonSerializer.SerializeToElement(structured)
            };
        }, ct);

    [McpServerTool(Name = "atlas_set_decoration", Title = "Nastavit ikonu stránky", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [Description("Nastaví ikonu a barvu stránky (obě naráz, identifikátory z atlas_list_appearance), nebo je odstraní (clear: true). Ikona je vlastnost " +
        "stránky: nikdy ji nevkládejte jako emoji do názvu ani textu. expectedRevisionId je revisionId z atlas_get_labels nebo z výsledku atlas_create_draft (nová podstránka často zdědí ikonu po rodiči či šabloně; vynechte jen u stránky, která ikonu opravdu nemá); " +
        "jiná hodnota je chyba decoration_changed s currentRevisionId. " + Immediate)]
    public Task<CallToolResult> SetDecorationAsync(
        [Description("pageId stránky.")] string pageId,
        [Description("Id ikony z atlas_list_appearance, například book-open. Spolu s color.")] string? icon = null,
        [Description("Id barvy z atlas_list_appearance, například blue-3. Spolu s icon.")] string? color = null,
        [Description("revisionId ikony z posledního čtení nebo z výsledku atlas_create_draft; vynechte, jen když stránka ikonu opravdu nemá.")] string? expectedRevisionId = null,
        [Description("true odstraní ikonu i barvu (icon a color pak vynechte).")] bool clear = false,
        CancellationToken ct = default)
        => RunAsync("atlas_set_decoration", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            var expected = McpArguments.OptionalId(expectedRevisionId, "expectedRevisionId");
            if (clear && (!string.IsNullOrEmpty(icon) || !string.IsNullOrEmpty(color)))
            {
                return McpErrors.InvalidArgument("S clear: true nezadávejte icon ani color.");
            }
            if (!clear && (string.IsNullOrEmpty(icon) || string.IsNullOrEmpty(color)))
            {
                return McpErrors.InvalidArgument("Zadejte icon i color (identifikátory z atlas_list_appearance), nebo clear: true.");
            }
            var state = await m_bus.InvokeAsync<PageDecorationState>(
                new SetMcpDecoration(id, new SetPageDecorationRequest(clear ? null : icon, clear ? null : color, expected)), ct);
            var text = new StringBuilder();
            text.Append(state.Decoration is { Icon: not null } ? "Ikona stránky nastavena. " : "Ikona stránky odstraněna. ").Append(Immediate).Append('\n');
            text.Append("- pageId: ").Append(id).Append('\n');
            if (state.Decoration is { } decoration)
            {
                text.Append("- ikona: ").Append(decoration.Icon ?? "žádná").Append(", barva: ").Append(decoration.Color ?? "žádná").Append('\n');
                text.Append("- expectedRevisionId (pro další zápis): ").Append(decoration.RevisionId).Append('\n');
            }
            return Written(text.ToString(), new JsonObject { ["pageId"] = id, ["decoration"] = DecorationNode(state.Decoration) });
        }, ct);

    [McpServerTool(Name = "atlas_set_tags", Title = "Nastavit tagy stránky", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [Description("Změní tagy stránky jedním ze dvou způsobů. (1) add a/nebo remove: přidá a odebere uvedené tagy podle názvu a ostatní zachová " +
        "(bezpečné i při souběžné úpravě; expectedTagIds volitelné). (2) tags: nahradí celou sadu (nejvýše 20 tagů) a vyžaduje expectedTagIds z " +
        "atlas_get_labels. Název tagu 1–48 znaků, velikost písmen se nerozlišuje. Zadejte buď tags, nebo add/remove. " + Immediate)]
    public Task<CallToolResult> SetTagsAsync(
        [Description("pageId stránky.")] string pageId,
        [Description("Tagy k přidání (názvy).")] string[]? add = null,
        [Description("Tagy k odebrání (názvy); tag, který stránka nemá, se přeskočí.")] string[]? remove = null,
        [Description("Celá nová sada tagů (nahradí stávající). Vyžaduje expectedTagIds.")] string[]? tags = null,
        [Description("id tagů stránky z posledního čtení (atlas_get_labels); povinné s tags, jinak volitelné.")] string[]? expectedTagIds = null,
        CancellationToken ct = default)
        => RunAsync("atlas_set_tags", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            Guid[]? expected = expectedTagIds?.Select(x => McpArguments.RequiredId(x, "expectedTagIds")).ToArray();
            PageTagState state;
            if (tags is not null)
            {
                if (add is not null || remove is not null)
                {
                    return McpErrors.InvalidArgument("Zadejte buď tags (celá sada), nebo add/remove, ne obojí.");
                }
                if (expected is null)
                {
                    return McpErrors.InvalidArgument("S tags je povinné expectedTagIds (id tagů z atlas_get_labels, prázdné pole pro stránku bez tagů). Pro přidání či odebrání bez nich použijte add/remove.");
                }
                state = await m_bus.InvokeAsync<PageTagState>(new SetMcpTags(id, new SetPageTagsRequest(tags, expected)), ct);
            }
            else if (add is null && remove is null)
            {
                return McpErrors.InvalidArgument("Zadejte add, remove, nebo tags.");
            }
            else
            {
                state = await m_bus.InvokeAsync<PageTagState>(new ChangeMcpTags(id, new ChangePageTagsRequest(add, remove, expected)), ct);
            }
            var text = new StringBuilder();
            text.Append("Tagy stránky uloženy. ").Append(Immediate).Append('\n');
            text.Append("- pageId: ").Append(id).Append('\n');
            text.Append("- tagy: ").Append(state.Tags.Count == 0 ? "žádné" : string.Join(", ", state.Tags.Select(x => McpText.Line(x.Name, 60)))).Append('\n');
            text.Append("- expectedTagIds (pro další zápis): ").Append(string.Join(", ", state.Tags.Select(x => x.Id))).Append('\n');
            return Written(text.ToString(), new JsonObject { ["pageId"] = id, ["tags"] = TagNodes(state.Tags) });
        }, ct);

    [McpServerTool(Name = "atlas_apply_labels", Title = "Hromadně změnit ikony a tagy", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Změní ikony, barvy a tagy až 100 stránek naráz, vše nebo nic. changes: pageId, icon+color nebo clearDecoration, addTags, removeTags, expectedRevisionId.")]
    [Description("Změní ikony, barvy a tagy až 100 stránek jedním voláním: jedna transakce, vše nebo nic. Položka changes: pageId; " +
        "volitelně icon + color (identifikátory z atlas_list_appearance) nebo clearDecoration: true, s expectedRevisionId jako u atlas_set_decoration; " +
        "volitelně addTags a removeTags (ostatní tagy zůstanou) s volitelným expectedTagIds. Co položka neuvádí, se nemění; každá stránka nejvýše jednou. " +
        "Když některá položka neprojde (decoration_changed, tags_changed, neplatná ikona či tag, not_found), neuloží se nic a chyba v items uvede " +
        "index, pageId a kód každé takové položky. " + Immediate)]
    public Task<CallToolResult> ApplyLabelsAsync(
        [Description("Změny, 1 až 100 položek.")] McpLabelChangeArgument[] changes,
        CancellationToken ct = default)
        => RunAsync("atlas_apply_labels", async () =>
        {
            if (changes is not { Length: >= 1 and <= MaxChanges })
            {
                return McpErrors.InvalidArgument($"changes musí mít 1 až {MaxChanges} položek.");
            }
            var parsed = new List<McpLabelChange>();
            for (var index = 0; index < changes.Length; index++)
            {
                var change = changes[index] ?? new McpLabelChangeArgument();
                var item = $"changes[{index}]";
                var id = McpArguments.RequiredId(change.PageId, item + ".pageId");
                if (parsed.Any(x => x.PageId == id))
                {
                    return McpErrors.InvalidArgument($"{item}: stránka už je v changes; spojte její změny do jedné položky.");
                }
                var appearance = !string.IsNullOrEmpty(change.Icon) || !string.IsNullOrEmpty(change.Color);
                if (change.ClearDecoration && appearance)
                {
                    return McpErrors.InvalidArgument($"{item}: s clearDecoration: true nezadávejte icon ani color.");
                }
                if (appearance && (string.IsNullOrEmpty(change.Icon) || string.IsNullOrEmpty(change.Color)))
                {
                    return McpErrors.InvalidArgument($"{item}: zadejte icon i color (identifikátory z atlas_list_appearance).");
                }
                var decoration = appearance || change.ClearDecoration
                    ? new SetPageDecorationRequest(change.ClearDecoration ? null : change.Icon, change.ClearDecoration ? null : change.Color,
                        McpArguments.OptionalId(change.ExpectedRevisionId, item + ".expectedRevisionId"))
                    : null;
                var tags = change.AddTags is not null || change.RemoveTags is not null
                    ? new ChangePageTagsRequest(change.AddTags, change.RemoveTags,
                        change.ExpectedTagIds?.Select(x => McpArguments.RequiredId(x, item + ".expectedTagIds")).ToArray())
                    : null;
                if (decoration is null && tags is null)
                {
                    return McpErrors.InvalidArgument($"{item}: nic ke změně; zadejte icon a color, clearDecoration, addTags nebo removeTags.");
                }
                parsed.Add(new McpLabelChange(id, decoration, tags));
            }
            var applied = await m_bus.InvokeAsync<IReadOnlyList<McpAppliedLabels>>(new ApplyMcpLabels(parsed), ct);
            var text = new StringBuilder();
            text.Append("Uloženo ").Append(applied.Count).Append(" změn (vše naráz). ").Append(Immediate).Append('\n');
            var items = new JsonArray();
            for (var index = 0; index < applied.Count; index++)
            {
                var page = applied[index];
                text.Append("- changes[").Append(index).Append("] pageId ").Append(page.PageId);
                var node = new JsonObject { ["index"] = index, ["pageId"] = page.PageId };
                if (page.Decoration is { } state)
                {
                    text.Append(": ikona ").Append(state.Decoration?.Icon ?? "žádná").Append('/').Append(state.Decoration?.Color ?? "žádná")
                        .Append(", revisionId ").Append(state.Decoration?.RevisionId.ToString() ?? "žádné");
                    node["decoration"] = DecorationNode(state.Decoration);
                }
                if (page.Tags is { } tags)
                {
                    text.Append("; tagy: ").Append(tags.Tags.Count == 0 ? "žádné" : string.Join(", ", tags.Tags.Select(x => McpText.Line(x.Name, 60))));
                    node["tags"] = TagNodes(tags.Tags);
                }
                text.Append('\n');
                items.Add(node);
            }
            return Written(text.ToString(), new JsonObject { ["items"] = items });
        }, ct);

    [McpServerTool(Name = "atlas_suggest_tags",Title = "Nabídnout tagy", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Najde existující tagy podle části názvu a vrátí jejich id. query; použij před atlas_find_pages_by_tag.")]
    [Description("Nabídne existující tagy (nejvýše 20), jejichž název obsahuje zadaný text, jen ze stránek, které vlastník tokenu smí číst a které " +
        "leží v prostorech zahrnutých do MCP. Vrací id a název; id použijte v atlas_find_pages_by_tag. " + McpText.TrustNotice)]
    public Task<CallToolResult> SuggestTagsAsync(
        [Description("Část názvu tagu; prázdné vrátí první tagy podle abecedy.")] string? query = null,
        CancellationToken ct = default)
        => RunAsync("atlas_suggest_tags", async () =>
        {
            var suggestions = await m_bus.InvokeAsync<TagSuggestions>(new SuggestTags(query, AiScopeOnly: true), ct);
            var body = suggestions.Items.Count == 0
                ? "Žádný tag."
                : string.Join("\n", suggestions.Items.Select((x, i) => $"{i + 1}. {McpText.Line(x.Name, 60)} (tagId: {x.Id})"));
            return McpText.Result(McpText.Wrap($"Tagy: {suggestions.Items.Count}.", body), new JsonObject { ["items"] = TagNodes(suggestions.Items) });
        }, ct);

    [McpServerTool(Name = "atlas_find_pages_by_tag", Title = "Stránky podle tagu", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Stránky s daným tagem. tagId z atlas_suggest_tags nebo atlas_get_labels, volitelně spaceId, limit, cursor.")]
    [Description("Vypíše stránky s daným tagem (tagId z atlas_suggest_tags nebo atlas_get_labels), které vlastník tokenu smí číst, volitelně jen v jednom " +
        "prostoru. Stránky prostorů vyloučených z MCP se nevypisují. Pokračování přes nextCursor. " + McpText.TrustNotice)]
    public Task<CallToolResult> FindPagesByTagAsync(
        [Description("tagId.")] string tagId,
        [Description("Volitelně jen v tomto prostoru (spaceId z atlas_list_spaces).")] string? spaceId = null,
        [Description("Počet výsledků 1–50, výchozí 20.")] int limit = 20,
        [Description("nextCursor z předchozího výsledku.")] string? cursor = null,
        CancellationToken ct = default)
        => RunAsync("atlas_find_pages_by_tag", async () =>
        {
            var tag = McpArguments.RequiredId(tagId, "tagId");
            var space = McpArguments.OptionalId(spaceId, "spaceId");
            if (limit is < 1 or > 50)
            {
                return McpErrors.InvalidArgument("limit musí být 1 až 50.");
            }
            var offset = McpCursor.Offset(cursor, "tagpages", 1_000);
            var response = await m_bus.InvokeAsync<SearchResponse>(
                new ListTaggedPages(tag, null, space, offset, limit, new SearchOptions("all", "any", "relevance"), AiScopeOnly: true), ct);
            var body = new StringBuilder();
            var items = new JsonArray();
            var number = 0;
            foreach (var hit in response.Items)
            {
                body.Append(++number).Append(". ").Append(McpText.Line(hit.Title, 200)).Append('\n');
                body.Append("   pageId: ").Append(hit.Id).Append(", spaceId: ").Append(hit.SpaceId).Append(", prostor: ").Append(McpText.Line(hit.SpaceName, 100))
                    .Append(hit.IsDraft ? ", má nepublikované změny" : "").Append('\n');
                items.Add(new JsonObject
                {
                    ["pageId"] = hit.Id, ["title"] = hit.Title, ["spaceId"] = hit.SpaceId, ["spaceName"] = hit.SpaceName, ["isDraft"] = hit.IsDraft
                });
            }
            var structured = new JsonObject { ["items"] = items };
            string? footer = null;
            if (response.NextOffset is { } next)
            {
                var nextCursor = McpCursor.Create("tagpages", new JsonObject { ["o"] = next });
                structured["nextCursor"] = nextCursor;
                footer = $"Další výsledky: zavolejte atlas_find_pages_by_tag se stejnými argumenty a cursor: \"{nextCursor}\".";
            }
            return McpText.Result(McpText.Wrap($"Stránky s tagem {tag}: {number}.", number == 0 ? "Žádné stránky." : body.ToString(), footer), structured);
        }, ct);

    private static JsonNode? DecorationNode(PageDecorationDto? decoration) => decoration is null
        ? null
        : new JsonObject { ["revisionId"] = decoration.RevisionId, ["icon"] = decoration.Icon, ["color"] = decoration.Color };

    private static JsonArray TagNodes(IEnumerable<TagDto> tags)
        => new(tags.Select(x => (JsonNode)new JsonObject { ["id"] = x.Id, ["name"] = x.Name }).ToArray());

    /// <summary>The result of a write: the page's own facts only (tag names are wiki-defined but shown as one-line values), so no wrapper.</summary>
    private static CallToolResult Written(string text, JsonObject structured) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        StructuredContent = JsonSerializer.SerializeToElement(structured)
    };

    private async Task<CallToolResult> RunAsync(string tool, Func<Task<CallToolResult>> body, CancellationToken ct)
    {
        try
        {
            return await body();
        }
        catch (ApiException error)
        {
            return McpErrors.From(error);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Never the exception text: an ORM or driver message can quote statement parameters, which hold tool arguments.
            m_logger.LogError("MCP tool {Tool} failed ({ErrorType})", tool, error.GetType().Name);
            return McpErrors.Error(500, "internal_error", "Nástroj se nepodařilo dokončit.");
        }
    }
}
