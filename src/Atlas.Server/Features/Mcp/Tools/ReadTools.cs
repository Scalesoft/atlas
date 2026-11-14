using System.ComponentModel;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;
using Atlas.Server.Features.Assistant.Agent;
using Atlas.Server.Features.Assistant.Sources;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Queries;
using Atlas.Server.Features.PageHistory.Queries;
using Atlas.Server.Features.PageLinks.Contracts;
using Atlas.Server.Features.PageLinks.Queries;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Features.Search.Queries;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.Mcp.Tools;

/// <summary>
/// The read tools of the MCP module (plan 2026-10-05 MCP §10, spec §5.1). Each call goes through the same Wolverine messages as the
/// web and REST reads, so content rights, the private draft rule, password references and locks are theirs; MCP only narrows them
/// by G5 (D10) and shapes the answer (McpText). Arguments are validated here and answer 400 invalid_argument; neither arguments nor
/// results are logged. Not a Wolverine handler: the class name does not end with "Handler".
/// </summary>
[McpServerToolType]
public sealed class McpReadTools
{
    private const int SpacePage = 50;
    private const int PageListPage = 100;
    private const int HistoryPage = 20;
    private const int BacklinkLimit = 50;
    private const int SearchRounds = 3;
    /// <summary>The longest passage text one search hit gives (a chunk is up to about 2 600 characters).</summary>
    private const int MaxPassage = 1_500;

    private readonly IMessageBus m_bus;
    private readonly McpSearchLimiter m_searchLimiter;
    private readonly ISemanticQuery m_semantic;
    private readonly ILogger<McpReadTools> m_logger;

    public McpReadTools(IMessageBus bus, McpSearchLimiter searchLimiter, ISemanticQuery semantic, ILogger<McpReadTools> logger)
    {
        m_bus = bus;
        m_searchLimiter = searchLimiter;
        m_semantic = semantic;
        m_logger = logger;
    }

    [McpServerTool(Name = "atlas_search", Title = "Hledat ve wiki", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Hledá v publikovaných stránkách podle významu i slov (snese překlepy). Obvykle první krok; query, volitelně spaceId, limit, cursor.")]
    [Description("Hledá v publikovaných stránkách wiki, ke kterým má vlastník tokenu přístup (odolné vůči překlepům; fráze v uvozovkách). " +
        "Vrací název, cestu, prostor, nadpis shody s headingId, úryvek a čas publikace; pokračování přes nextCursor. " +
        McpText.TrustNotice)]
    public Task<CallToolResult> SearchAsync(ClaimsPrincipal user,
        [Description("Hledaný text.")] string query,
        [Description("Volitelně jen v tomto prostoru (spaceId z atlas_list_spaces).")] string? spaceId = null,
        [Description("Počet výsledků 1–20, výchozí 10.")] int limit = 10,
        [Description("nextCursor z předchozího výsledku.")] string? cursor = null,
        CancellationToken ct = default)
        => RunAsync("atlas_search", async () =>
        {
            var text = query?.Trim() ?? "";
            if (text.Length == 0)
            {
                return McpErrors.InvalidArgument("Zadejte hledaný text.");
            }
            text = AnswerQueryText.Keywords(text);
            if (limit is < 1 or > 20)
            {
                return McpErrors.InvalidArgument("limit musí být 1 až 20.");
            }
            var space = McpArguments.OptionalId(spaceId, "spaceId");
            var offset = McpCursor.Offset(cursor, "search", 1_000);
            // Whether the first window was relaxed (S2-3): its next windows continue that result without probing the strict query.
            bool? relaxed = McpCursor.Read(cursor, "search")?["r"] is JsonValue flag && flag.TryGetValue<bool>(out var known) ? known : null;
            // Per token over /mcp; the in-process assistant has no token and is limited per signed-in user.
            var budget = user.FindFirst(PersonalApiAuthentication.TokenClaim)?.Value
                ?? (user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value is { } userId ? "user:" + userId : null)
                ?? throw new ApiException(401, "Platný osobní API token je vyžadován.");
            if (m_searchLimiter.TryAcquire(budget) is { } retryAfter)
            {
                return McpErrors.Error(429, "rate_limited", $"Příliš mnoho hledání. Zkuste to znovu za {retryAfter} s.",
                    new JsonObject { ["retryAfter"] = retryAfter });
            }
            // Hybrid like /api/v1/search when semantic search is available (MCP spec §1: "MCP ho použije automaticky"), lexical
            // otherwise. The query is embedded once, outside the handler's transaction (as SearchEndpoints.SearchAsync does), and
            // every window uses the same vector or none, so the windows continue one result order.
            var options = new SearchOptions(Content: "published", AiScopeOnly: true);
            var vector = await m_semantic.TryEmbedAsync(text, options, true, offset, ct);
            // G5 removes hits after each window (SearchOptions.AiScopeOnly); a few more windows keep a result page full.
            var hits = new List<SearchHit>();
            int? next = offset;
            for (var round = 0; round < SearchRounds && next is { } from && hits.Count < limit; round++)
            {
                var response = await m_bus.InvokeAsync<SearchResponse>(
                    new Search.Queries.Search(text, space, from, limit - hits.Count, options) { Semantic = vector, Relaxed = relaxed }, ct);
                hits.AddRange(response.Items);
                next = response.NextOffset;
                relaxed = response.Relaxed;
            }
            var paths = hits.Count == 0
                ? new Dictionary<Guid, string>()
                : (await m_bus.InvokeAsync<McpPaths>(new GetMcpPaths(hits.Select(hit => new McpPathRequest(hit.Id, hit.SpaceName, hit.Title)).ToArray()), ct)).Items;
            var nextCursor = next is { } nextOffset ? McpCursor.Create("search", new JsonObject { ["o"] = nextOffset, ["r"] = relaxed == true }) : null;

            var items = new JsonArray();
            var entries = new List<(string Head, string? Passage, string Snippet)>();
            foreach (var hit in hits)
            {
                var path = paths.GetValueOrDefault(hit.Id) ?? hit.SpaceName + " › " + hit.Title;
                var head = new StringBuilder();
                head.Append(McpText.Line(hit.Title, 200)).Append('\n');
                head.Append("   pageId: ").Append(hit.Id).Append(", spaceId: ").Append(hit.SpaceId).Append('\n');
                head.Append("   cesta: ").Append(McpText.Line(path, 400)).Append('\n');
                // A hit whose headings do not name the query is still placed in its passage's section (e.g. a requirement block's heading).
                var (sectionTitle, sectionId) = hit.HeadingId is not null ? (hit.HeadingTitle, hit.HeadingId) : (hit.Passage?.HeadingTitle, hit.Passage?.HeadingId);
                if (sectionId is not null)
                {
                    head.Append("   sekce: ").Append(McpText.Line(sectionTitle, 200)).Append(" (headingId: ").Append(McpText.Line(sectionId, 130)).Append(")\n");
                }
                if (hit.PublishedAt is { } published)
                {
                    head.Append("   publikováno: ").Append(Date(published)).Append('\n');
                }
                entries.Add((head.ToString(), hit.PassageBody is { Length: > 0 } passage ? McpText.Content(SearchText.Passage(passage, MaxPassage)) : null,
                    McpText.Line(hit.Snippet, 400)));
                var item = new JsonObject
                {
                    ["pageId"] = hit.Id, ["title"] = hit.Title, ["spaceId"] = hit.SpaceId, ["spaceName"] = hit.SpaceName, ["path"] = path,
                    ["snippet"] = hit.Snippet
                };
                if (sectionId is not null)
                {
                    item["headingTitle"] = sectionTitle;
                    item["headingId"] = sectionId;
                }
                if (hit.PublishedAt is { } at)
                {
                    item["publishedAt"] = at;
                }
                items.Add(item);
            }
            var heading = $"Výsledky hledání {McpText.Quote(McpText.Line(text, 200))} v publikovaných stránkách: {hits.Count}."
                + (relaxed == true && hits.Count > 0 ? " (nenalezeno pro všechna slova dotazu; výsledky obsahují aspoň jedno z nich)" : "");
            var footer = nextCursor is null ? null : $"Další výsledky: zavolejte atlas_search se stejným dotazem a cursor: \"{nextCursor}\".";
            var structured = new JsonObject { ["items"] = items, ["relaxed"] = relaxed == true };
            if (nextCursor is not null)
            {
                structured["nextCursor"] = nextCursor;
            }
            // The passages share what the assistant's output cap leaves after everything else (the text duplicate in the structured
            // content is the first thing the cap drops), the best-ranked hits first.
            var rest = AssistantToolSession.OutputCap - 300 - structured.ToJsonString().Length
                - McpText.Wrap(heading, SearchBody(entries, []), footer).Length;
            var body = hits.Count == 0 ? "Nic nenalezeno." : SearchBody(entries, PassageLengths(entries.Select(entry => entry.Passage?.Length ?? 0).ToArray(), rest));
            return McpText.Result(McpText.Wrap(heading, body, footer), structured);
        }, ct);

    /// <summary>The hits as numbered entries: the passage text where the hit has one (cut to its length), otherwise the snippet.</summary>
    private static string SearchBody(IReadOnlyList<(string Head, string? Passage, string Snippet)> entries, IReadOnlyList<int> lengths)
    {
        var body = new StringBuilder();
        for (var index = 0; index < entries.Count; index++)
        {
            var (head, passage, snippet) = entries[index];
            body.Append(index + 1).Append(". ").Append(head);
            var length = index < lengths.Count ? lengths[index] : 0;
            body.Append(passage is null || length == 0 ? "   úryvek: " + snippet : "   text: " + SearchText.Passage(passage, length)).Append('\n');
        }
        return body.ToString();
    }

    /// <summary>
    /// How much of each passage fits into <paramref name="budget"/> characters, in rank order: a hit gets up to
    /// <see cref="MaxPassage"/> characters while every later passage keeps at least <see cref="SearchText.PassageLength"/>.
    /// </summary>
    public static int[] PassageLengths(IReadOnlyList<int> available, int budget)
    {
        var lengths = new int[available.Count];
        var later = available.Count(length => length > 0);
        for (var index = 0; index < available.Count; index++)
        {
            if (available[index] == 0)
            {
                continue;
            }
            later--;
            var share = Math.Clamp(budget - later * SearchText.PassageLength, SearchText.PassageLength, MaxPassage);
            lengths[index] = Math.Min(available[index], share);
            budget -= lengths[index];
        }
        return lengths;
    }

    [McpServerTool(Name = "atlas_list_spaces", Title = "Prostory wiki", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Vypíše prostory, které uživatel vidí (spaceId, název, role). Použij, když potřebuješ spaceId nebo přehled prostorů.")]
    [Description("Vypíše prostory wiki dostupné vlastníkovi tokenu: spaceId, název, popis a jeho roli. Pokračování přes nextCursor. " + McpText.TrustNotice)]
    public Task<CallToolResult> ListSpacesAsync(
        [Description("nextCursor z předchozího výsledku.")] string? cursor = null,
        CancellationToken ct = default)
        => RunAsync("atlas_list_spaces", async () =>
        {
            var offset = McpCursor.Offset(cursor, "spaces");
            var list = await m_bus.InvokeAsync<McpSpaceList>(new ListMcpSpaces(offset, SpacePage), ct);
            var body = new StringBuilder();
            var items = new JsonArray();
            var shown = 0;
            foreach (var space in list.Items)
            {
                var entry = new StringBuilder();
                entry.Append("- ").Append(McpText.Line(space.Name, 200)).Append(" (spaceId: ").Append(space.SpaceId).Append(", role: ").Append(space.Role).Append(")\n");
                if (space.Description.Length > 0)
                {
                    entry.Append("  ").Append(McpText.Line(space.Description, 500)).Append('\n');
                }
                if (body.Length + entry.Length > McpText.Limit - 2_000)
                {
                    break;
                }
                body.Append(entry);
                items.Add(new JsonObject { ["spaceId"] = space.SpaceId, ["name"] = space.Name, ["description"] = space.Description, ["role"] = space.Role });
                shown++;
            }
            var more = list.HasMore || shown < list.Items.Count;
            var nextCursor = more ? McpCursor.Create("spaces", new JsonObject { ["o"] = offset + shown }) : null;
            var structured = new JsonObject { ["items"] = items };
            if (nextCursor is not null)
            {
                structured["nextCursor"] = nextCursor;
            }
            return McpText.Result(McpText.Wrap($"Prostory: {shown}.", shown == 0 ? "Žádný dostupný prostor." : body.ToString(),
                nextCursor is null ? null : $"Další prostory: cursor \"{nextCursor}\"."), structured);
        }, ct);

    [McpServerTool(Name = "atlas_list_pages", Title = "Stránky prostoru", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Jedna úroveň stránek prostoru (bez parentId kořen) s treeVersion a version, které potřebují přesuny. spaceId, volitelně parentId.")]
    [Description("Vypíše jednu úroveň stromu stránek prostoru (bez parentId kořen, jinak podstránky dané stránky): pageId, název, rodič, " +
        "zda má podstránky a zda je publikovaná; editorovi i to, zda má nepublikované změny nebo neuložené živé úpravy. " +
        "Pro organizaci stránek vrací treeVersion prostoru (expectedTreeVersion) a u každé stránky version (expectedVersion), position mezi sourozenci a canEdit. " + McpText.TrustNotice)]
    public Task<CallToolResult> ListPagesAsync(
        [Description("spaceId z atlas_list_spaces.")] string spaceId,
        [Description("Volitelně pageId stránky, jejíž podstránky vypsat.")] string? parentId = null,
        [Description("nextCursor z předchozího výsledku.")] string? cursor = null,
        CancellationToken ct = default)
        => RunAsync("atlas_list_pages", async () =>
        {
            var space = McpArguments.RequiredId(spaceId, "spaceId");
            var parent = McpArguments.OptionalId(parentId, "parentId");
            var offset = McpCursor.Offset(cursor, "pages");
            var tree = await m_bus.InvokeAsync<PageTreeDto>(new GetPageTree(space), ct);
            if (parent is { } parentPage && tree.Items.All(item => item.Id != parentPage))
            {
                throw new ApiException(404, McpVisibility.MissingPage);
            }
            var withChildren = tree.Items.Where(item => item.ParentId is not null).Select(item => item.ParentId!.Value).ToHashSet();
            var level = tree.Items.Where(item => item.ParentId == parent && item.Readable).ToArray();
            var window = level.Skip(offset).Take(PageListPage).ToArray();
            // Also the G5 decision for the space: an excluded one answers 404 here, before anything is returned.
            var states = (await m_bus.InvokeAsync<McpPageStates>(new GetMcpPageStates(space, window.Where(item => item.CanEdit).Select(item => item.Id).ToArray()), ct))
                .Items.ToDictionary(state => state.PageId);
            var body = new StringBuilder();
            var items = new JsonArray();
            var shown = 0;
            foreach (var page in window)
            {
                var entry = new StringBuilder();
                entry.Append("- ").Append(McpText.Line(page.Title, 200)).Append(" (pageId: ").Append(page.Id).Append(", pořadí ").Append(offset + shown + 1)
                    .Append(", verze ").Append(page.Version);
                entry.Append(page.IsPublished ? ", publikovaná" : ", nepublikovaná");
                if (withChildren.Contains(page.Id))
                {
                    entry.Append(", má podstránky");
                }
                var item = new JsonObject
                {
                    ["pageId"] = page.Id, ["title"] = page.Title, ["parentId"] = page.ParentId, ["hasChildren"] = withChildren.Contains(page.Id),
                    ["isPublished"] = page.IsPublished, ["version"] = page.Version, ["position"] = offset + shown + 1, ["canEdit"] = page.CanEdit
                };
                if (states.TryGetValue(page.Id, out var state))
                {
                    item["hasUnpublishedChanges"] = state.HasUnpublishedChanges;
                    item["hasUnsavedLiveEdits"] = state.HasUnsavedLiveEdits;
                    if (state.HasUnpublishedChanges)
                    {
                        entry.Append(", nepublikované změny");
                    }
                    if (state.HasUnsavedLiveEdits)
                    {
                        entry.Append(", neuložené živé úpravy");
                    }
                }
                entry.Append(")\n");
                if (body.Length + entry.Length > McpText.Limit - 2_000)
                {
                    break;
                }
                body.Append(entry);
                items.Add(item);
                shown++;
            }
            var more = offset + shown < level.Length;
            var nextCursor = more ? McpCursor.Create("pages", new JsonObject { ["o"] = offset + shown }) : null;
            var structured = new JsonObject { ["spaceId"] = space, ["parentId"] = parent, ["treeVersion"] = tree.Version, ["items"] = items };
            if (nextCursor is not null)
            {
                structured["nextCursor"] = nextCursor;
            }
            var heading = parent is null ? $"Kořenové stránky prostoru {space}: {shown} z {level.Length}." : $"Podstránky stránky {parent}: {shown} z {level.Length}.";
            return McpText.Result(McpText.Wrap(heading + $" treeVersion: {tree.Version.ToString(CultureInfo.InvariantCulture)}.", shown == 0 ? "Žádné stránky." : body.ToString(),
                nextCursor is null ? null : $"Další stránky: stejné parametry a cursor \"{nextCursor}\"."), structured);
        }, ct);

    [McpServerTool(Name = "atlas_get_page", Title = "Přečíst stránku", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Přečte publikovanou stránku jako Markdown se seznamem sekcí; s version: \"working\" rozepsanou pracovní verzi (jen editorovi stránky). " +
        "pageId, volitelně headingId pro jednu sekci a cursor pro pokračování dlouhé stránky.")]
    [Description("Přečte stránku wiki jako Markdown: publikovanou verzi (výchozí), nebo pracovní verzi (version: \"working\", jen pokud smíte stránku upravovat). " +
        "Vrací název, cestu, verzi, expectedVersion pro zápis (jen editorovi), sekce s headingId a komponenty jako zástupné značky ⟦c:N⟧. " +
        "Obrázky příloh přicházejí jako Markdown ![popis](url), karty souborů jako zástupné značky s položkou embeds (attachmentId, occurrence). " +
        "S headingId vrátí jen tu sekci včetně podsekcí. Dlouhá stránka pokračuje přes nextCursor. " + McpText.TrustNotice)]
    public Task<CallToolResult> GetPageAsync(
        [Description("pageId stránky.")] string pageId,
        [Description("\"published\" (výchozí) nebo \"working\".")] string version = "published",
        [Description("Volitelně headingId sekce ze seznamu sekcí.")] string? headingId = null,
        [Description("nextCursor z předchozího výsledku.")] string? cursor = null,
        CancellationToken ct = default)
        => RunAsync("atlas_get_page", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            if (version is not ("published" or "working"))
            {
                return McpErrors.InvalidArgument("version musí být \"published\" nebo \"working\".");
            }
            if (headingId is { Length: > 128 })
            {
                return McpErrors.InvalidArgument("headingId je příliš dlouhé.");
            }
            McpPageRead read;
            try
            {
                read = await m_bus.InvokeAsync<McpPageRead>(new GetMcpPage(id, version == "working"), ct);
            }
            catch (ApiException error) when (error.Code == "not_published")
            {
                throw new ApiException(404, error.Message + " Pracovní verzi přečtete s version: \"working\".", error.Code);
            }
            // The projection runs after the read transaction, never under the page locks.
            var projection = RichMarkdownProjection.Project(RichDocument.Parse(read.Page.ContentJson),
                new ProjectionOptions(read.ProjectedVersion, page => read.ReadablePageTitles.GetValueOrDefault(page)));
            return McpPageOutput.Render(read, projection, string.IsNullOrEmpty(headingId) ? null : headingId, cursor);
        }, ct);

    [McpServerTool(Name = "atlas_get_backlinks", Title = "Odkazuje sem", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Publikované stránky, které odkazují na danou stránku (až 50). pageId cílové stránky.")]
    [Description("Vypíše publikované stránky, které odkazují na danou stránku a které smí vlastník tokenu číst (nejvýše 50). " + McpText.TrustNotice)]
    public Task<CallToolResult> GetBacklinksAsync(
        [Description("pageId cílové stránky.")] string pageId,
        CancellationToken ct = default)
        => RunAsync("atlas_get_backlinks", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            var connections = await m_bus.InvokeAsync<PageConnections>(new GetPageConnections(id, BacklinkLimit), ct);
            var scope = await m_bus.InvokeAsync<McpScope>(new CheckMcpScope([id], connections.LinksHere.Items.Select(x => x.SpaceId).Distinct().ToArray()), ct);
            if (!scope.PagesInScope.Contains(id))
            {
                throw new ApiException(404, McpVisibility.MissingPage);
            }
            var linking = connections.LinksHere.Items.Where(x => !scope.ExcludedSpaces.Contains(x.SpaceId)).ToArray();
            var body = new StringBuilder();
            var items = new JsonArray();
            foreach (var page in linking)
            {
                body.Append("- ").Append(McpText.Line(page.Title, 200)).Append(" (pageId: ").Append(page.Id).Append(", prostor: ")
                    .Append(McpText.Line(page.SpaceName, 200)).Append(")\n");
                items.Add(new JsonObject { ["pageId"] = page.Id, ["title"] = page.Title, ["spaceId"] = page.SpaceId, ["spaceName"] = page.SpaceName });
            }
            var footer = connections.LinksHere.Items.Count >= BacklinkLimit ? $"Zobrazeno nejvýše {BacklinkLimit} odkazujících stránek." : null;
            return McpText.Result(McpText.Wrap($"Stránky odkazující na {id}: {linking.Length}.", linking.Length == 0 ? "Žádná." : body.ToString(), footer),
                new JsonObject { ["pageId"] = id, ["items"] = items });
        }, ct);

    [McpServerTool(Name = "atlas_get_history", Title = "Historie stránky", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Verze stránky od nejnovější: revisionId, autor, kanál, čas, publikace, změna s AI, rozsah změn a přispěvatelé; s group jednotlivá uložení verze. Jen pro toho, kdo smí stránku upravovat. pageId, group, cursor.")]
    [Description("Vypíše verze stránky od nejnovější. Verze slučuje průběžná uložení (pauza 30 minut, nejvýše 4 hodiny, nový den; publikace, obnova, " +
        "pojmenovaná verze a změna s AI začínají novou). U verze: číslo poslední revize, autor, kanál (web, REST API, MCP), čas, zda byla publikována, " +
        "popis změny publikace, název verze, zda jde o změnu s AI, počet uložení, rozsah změn (řádky, bloky, sekce, stavy požadavků) a přispěvatelé. " +
        "U každé revisionId (pro atlas_undo_change a atlas_restore_version) a groupId; s group vypíše jednotlivá uložení té verze. " +
        "Jen pro toho, kdo smí stránku upravovat. Pokračování přes nextCursor. " + McpText.TrustNotice)]
    public Task<CallToolResult> GetHistoryAsync(
        [Description("pageId stránky.")] string pageId,
        [Description("nextCursor z předchozího výsledku.")] string? cursor = null,
        [Description("groupId verze: vypíše její jednotlivá uložení místo verzí (s cursor ho zopakujte).")] string? group = null,
        CancellationToken ct = default)
        => RunAsync("atlas_get_history", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            var groupId = McpArguments.OptionalId(group, "group");
            var position = McpCursor.Read(cursor, "history");
            int? before = position is null ? null : McpCursor.Int(position, "b", 1, int.MaxValue);
            var scope = await m_bus.InvokeAsync<McpScope>(new CheckMcpScope([id], []), ct);
            if (!scope.PagesInScope.Contains(id))
            {
                throw new ApiException(404, McpVisibility.MissingPage);
            }
            var history = await m_bus.InvokeAsync<PageHistoryList>(new GetPageHistory(id, before, HistoryPage, groupId), ct);
            var body = new StringBuilder();
            var items = new JsonArray();
            foreach (var revision in history.Items)
            {
                body.Append("- verze ").Append(revision.Version.ToString(CultureInfo.InvariantCulture)).Append(" · ").Append(Date(revision.ActiveFrom))
                    .Append(" · ").Append(McpText.Line(revision.AuthorName, 120)).Append(" · ").Append(ChannelLabel(revision.Channel));
                if (revision.ApiToken is { } token)
                {
                    body.Append(" (token ").Append(McpText.Quote(McpText.Line(token.Name, 100))).Append(')');
                }
                if (revision.IsPublished)
                {
                    body.Append(" · publikovaná");
                }
                if (revision.IsCurrent)
                {
                    body.Append(" · aktuální");
                }
                if (revision.IsAi)
                {
                    body.Append(" · změna s AI");
                }
                if (revision.Checkpoints > 1)
                {
                    body.Append(" · ").Append(revision.Checkpoints.ToString(CultureInfo.InvariantCulture)).Append(" uložení od ").Append(Date(revision.StartedAt));
                }
                body.Append('\n').Append("  revisionId: ").Append(revision.Id).Append(", groupId: ").Append(revision.GroupId).Append('\n');
                if (revision.Label is { Length: > 0 } label)
                {
                    body.Append("  název verze: ").Append(McpText.Line(label, 500)).Append('\n');
                }
                if (revision.ChangeSummary is { Length: > 0 } summary)
                {
                    body.Append("  popis změny: ").Append(McpText.Line(summary, 500)).Append('\n');
                }
                if (revision.Changes is { } changes)
                {
                    body.Append("  změny: +").Append(changes.Added.ToString(CultureInfo.InvariantCulture)).Append(" −")
                        .Append(changes.Removed.ToString(CultureInfo.InvariantCulture)).Append(" řádků, bloků ").Append(changes.Blocks.ToString(CultureInfo.InvariantCulture));
                    if (changes.Sections.Count > 0)
                    {
                        body.Append("; sekce: ").Append(string.Join(", ", changes.Sections.Select(x => McpText.Quote(McpText.Line(x, 120)))));
                    }
                    if (changes.States.Count > 0)
                    {
                        body.Append("; stavy požadavků: ").Append(string.Join(", ", changes.States.Select(x => $"{McpText.Quote(McpText.Line(x.Title, 120))} {x.From} → {x.To}")));
                    }
                    if (changes.Contributors.Count > 1)
                    {
                        body.Append("; přispěvatelé: ").Append(string.Join(", ", changes.Contributors.Select(x => McpText.Line(x.Name ?? "?", 120))));
                    }
                    body.Append('\n');
                }
                var item = new JsonObject
                {
                    ["revisionId"] = revision.Id, ["groupId"] = revision.GroupId, ["previousRevisionId"] = revision.PreviousRevisionId,
                    ["version"] = revision.Version, ["title"] = revision.Title, ["authorName"] = revision.AuthorName, ["channel"] = revision.Channel,
                    ["at"] = revision.ActiveFrom, ["isPublished"] = revision.IsPublished, ["isCurrent"] = revision.IsCurrent
                };
                if (revision.ApiToken is { } apiToken)
                {
                    item["apiTokenName"] = apiToken.Name;
                }
                if (revision.ChangeSummary is { Length: > 0 } changeSummary)
                {
                    item["changeSummary"] = changeSummary;
                }
                item["isAi"] = revision.IsAi;
                item["checkpoints"] = revision.Checkpoints;
                item["startedAt"] = revision.StartedAt;
                if (revision.Label is { Length: > 0 } versionLabel)
                {
                    item["label"] = versionLabel;
                }
                if (revision.Changes is { } versionChanges)
                {
                    item["changes"] = new JsonObject
                    {
                        ["added"] = versionChanges.Added, ["removed"] = versionChanges.Removed, ["blocks"] = versionChanges.Blocks,
                        ["sections"] = new JsonArray(versionChanges.Sections.Select(x => (JsonNode)x).ToArray()),
                        ["states"] = new JsonArray(versionChanges.States.Select(x => (JsonNode)new JsonObject { ["title"] = x.Title, ["from"] = x.From, ["to"] = x.To }).ToArray()),
                        ["contributors"] = new JsonArray(versionChanges.Contributors.Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["checkpoints"] = x.Checkpoints }).ToArray())
                    };
                }
                items.Add(item);
            }
            var nextCursor = history.NextBeforeVersion is { } boundary ? McpCursor.Create("history", new JsonObject { ["b"] = boundary }) : null;
            var structured = new JsonObject { ["pageId"] = id, ["currentVersion"] = history.CurrentVersion, ["items"] = items };
            if (nextCursor is not null)
            {
                structured["nextCursor"] = nextCursor;
            }
            return McpText.Result(McpText.Wrap($"Historie stránky {id} (aktuální verze {history.CurrentVersion}).",
                history.Items.Count == 0 ? "Žádné verze." : body.ToString(),
                nextCursor is null ? null : $"Starší verze: cursor \"{nextCursor}\"."), structured);
        }, ct);

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

    private static string Date(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string ChannelLabel(string? channel) => channel switch
    {
        "web" => "web",
        "api" => "přes REST API",
        "mcp" => "přes MCP",
        "assistant" => "přes asistenta",
        "import" => "import",
        "system" => "systém",
        _ => "kanál neznámý (před 2.0)"
    };
}
