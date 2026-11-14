using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PersonalApi;

namespace Atlas.Server.Features.Mcp.Tools;

/// <summary>
/// The page organization tools of the MCP module (spec section 4): move under a parent, to the root or among siblings, move to
/// another space, move to the recoverable trash, list the trash and restore. Each runs the shared handler of the web and REST in its
/// own transaction (<see cref="MoveMcpPage"/> and the others), so the rights, the compare-and-set on the tree versions and the page
/// version, cycles, hidden parents, the explicit subtree scope, the unsaved live edits and the audit channel are the REST ones; MCP
/// adds G5 under the locks. The writes need the scope write as on REST: they change at once what readers see (plan D2), so they are
/// not drafts and wait for no human confirmation, but every one is undone by another call. One page per call, with the tree version it
/// returns for the next one; the one batch is atlas_arrange_pages (a whole menu of one space, all or nothing). Every attempt is audited by the call filter (<see cref="McpToolScopes"/>). Not a Wolverine
/// handler: the class name does not end with "Handler".
/// </summary>
[McpServerToolType]
public sealed class McpOrganizationTools
{
    private const int TrashWindow = 50;

    private const string Immediate = "Změna je čtenářům viditelná okamžitě (netýká se konceptu, nečeká na publikaci) a jde vrátit: " +
        "přesun dalším přesunem, koš obnovením (atlas_restore_page).";

    private const string Versions = "Verze (expectedTreeVersion, expectedVersion) vezměte z atlas_list_pages; při konfliktu nic nezměníme a strom je třeba načíst znovu.";

    private readonly IMessageBus m_bus;
    private readonly McpAddress m_address;
    private readonly ILogger<McpOrganizationTools> m_logger;

    public McpOrganizationTools(IMessageBus bus, McpAddress address, ILogger<McpOrganizationTools> logger)
    {
        m_bus = bus;
        m_address = address;
        m_logger = logger;
    }

    [McpServerTool(Name = "atlas_move_page", Title = "Přesunout nebo seřadit stránku", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Přesune stránku i s podstránkami v jejím prostoru pod parentId (bez něj na kořen), před beforePageId. pageId, expectedTreeVersion z atlas_list_pages.")]
    [Description("Přesune stránku (i s podstránkami) v rámci jejího prostoru pod rodičovskou stránku (parentId), nebo na kořen (parentId vynechte), " +
        "a určí pořadí mezi sourozenci: před stránku beforePageId, bez ní na konec. Změna jen pořadí = stejný rodič a jiný beforePageId. " +
        "Skupinu stránek sloučíte pod společný rozcestník tak, že pro každou zavoláte tento nástroj s parentId rozcestníku; po každém volání " +
        "použijte vrácený treeVersion jako další expectedTreeVersion. Cyklus (pod sebe či vlastní podstránku) a rodiče, které nevidíte nebo " +
        "nesmíte upravovat, Atlas odmítne. " + Immediate + " " + Versions)]
    public Task<CallToolResult> MovePageAsync(
        [Description("pageId přesouvané stránky.")] string pageId,
        [Description("expectedTreeVersion: treeVersion prostoru z posledního atlas_list_pages.")] long expectedTreeVersion,
        [Description("pageId nové rodičovské stránky ve stejném prostoru; bez něj kořen prostoru.")] string? parentId = null,
        [Description("pageId sourozence, před kterého se stránka zařadí; bez něj na konec úrovně.")] string? beforePageId = null,
        CancellationToken ct = default)
        => RunAsync("atlas_move_page", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            RequireTreeVersion(expectedTreeVersion, "expectedTreeVersion");
            var parent = McpArguments.OptionalId(parentId, "parentId");
            var before = McpArguments.OptionalId(beforePageId, "beforePageId");
            var done = await m_bus.InvokeAsync<McpOrganizationResult>(new MoveMcpPage(id, new MovePageRequest(parent, before, expectedTreeVersion)), ct);
            return Changed("Stránka přesunuta.", id, done, null);
        }, ct);

    [McpServerTool(Name = "atlas_arrange_pages", Title = "Uspořádat stránky prostoru", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Uspořádá strom (levé menu) jednoho prostoru: až 100 přesunů naráz, vše nebo nic. moves: pageId, parentId (bez něj kořen), beforePageId (bez něj na konec); expectedTreeVersion z atlas_get_space_tree.")]
    [Description("Uspořádá stránky jednoho prostoru (levé menu) jedním voláním: až 100 přesunů, které se provedou postupně v jedné transakci, vše nebo nic. " +
        "Položka moves: pageId, volitelně parentId (bez něj kořen prostoru) a beforePageId (sourozenec pod tímtéž rodičem, před kterého se stránka zařadí; " +
        "bez něj na konec úrovně). Každý přesun už vidí ty předchozí, takže nejdřív přesuňte rozcestníky a pak stránky pod ně a pořadí skládejte odshora. " +
        "Podstránky se přesouvají se svou stránkou; každá stránka nejvýše jednou. Když některý přesun neprojde, nepřesune se nic a chyba v items uvede " +
        "jeho index, pageId a kód. " + Immediate + " " + Versions)]
    public Task<CallToolResult> ArrangePagesAsync(
        [Description("expectedTreeVersion: treeVersion prostoru z posledního atlas_list_pages nebo atlas_get_space_tree.")] long expectedTreeVersion,
        [Description("Přesuny v pořadí provedení, 1 až 100 položek.")] McpPageMoveArgument[] moves,
        CancellationToken ct = default)
        => RunAsync("atlas_arrange_pages", async () =>
        {
            RequireTreeVersion(expectedTreeVersion, "expectedTreeVersion");
            var parsed = McpPageMove.ParseAll(moves);
            var done = await m_bus.InvokeAsync<McpOrganizationResult>(new ArrangeMcpPages(parsed, expectedTreeVersion), ct);
            return Changed($"Přesunuto {parsed.Count} stránek (vše naráz).", parsed[0].PageId, done, null);
        }, ct);

    [McpServerTool(Name = "atlas_sort_pages", Title = "Řadit stránky abecedně", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Zapne či vypne „Řadit abecedně“ jedné úrovně levého menu: podstránky parentId, bez něj kořen prostoru. spaceId, byTitle.")]
    [Description("Zapne nebo vypne „Řadit abecedně“ pro jednu úroveň levého menu (stromu prostoru): přímé podstránky stránky parentId, bez něj " +
        "kořen prostoru. Zapnutá úroveň se drží seřazená podle názvů (česky, bez ohledu na velikost písmen), i když do ní přibude, přejmenuje " +
        "se nebo se do ní přesune stránka; atlas_get_space_tree ji ukazuje jako childrenByTitle (kořen rootByTitle). Ruční přesun v rámci " +
        "takové úrovně (atlas_move_page nebo atlas_arrange_pages se stejným rodičem) řazení vypne a ponechá zobrazené pořadí; výsledek pak " +
        "uvede titleOrderCancelled: true. Vypnutím zůstane pořadí, jak bylo zobrazené. Vyžaduje právo upravovat rodičovskou stránku, pro " +
        "kořen roli Writer v prostoru. " + Immediate)]
    public Task<CallToolResult> SortPagesAsync(
        [Description("spaceId z atlas_list_spaces.")] string spaceId,
        [Description("true = řadit abecedně, false = vypnout.")] bool byTitle,
        [Description("pageId stránky, jejíž přímé podstránky se řadí; bez něj kořen prostoru.")] string? parentId = null,
        CancellationToken ct = default)
        => RunAsync("atlas_sort_pages", async () =>
        {
            var space = McpArguments.RequiredId(spaceId, "spaceId");
            var parent = McpArguments.OptionalId(parentId, "parentId");
            var done = await m_bus.InvokeAsync<McpOrganizationResult>(new SortMcpTreeLevel(space, new SortTreeLevelRequest(parent, byTitle)), ct);
            var level = parent is null ? "kořen prostoru" : $"podstránky stránky {parent}";
            var text = $"„Řadit abecedně“ je {(byTitle ? "zapnuté" : "vypnuté")} pro {level}. {Immediate}\n" +
                $"- spaceId: {space}\n- treeVersion (expectedTreeVersion pro další změnu tohoto prostoru): {Number(done.Change.Version)}\n";
            return Result(text, new JsonObject
            {
                ["spaceId"] = space, ["parentId"] = parent, ["byTitle"] = byTitle, ["treeVersion"] = done.Change.Version, ["expectedTreeVersion"] = done.Change.Version
            });
        }, ct);

    [McpServerTool(Name = "atlas_move_page_to_space", Title = "Přesunout stránku do jiného prostoru", Destructive = true, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Přesune stránku do jiného prostoru, přístup převezme cílový prostor. pageId, targetSpaceId, verze stromů a stránky, includeSubtree, acknowledgeAccessChange.")]
    [Description("Přesune stránku do jiného prostoru (volitelně pod rodiče cílového prostoru a před sourozence). Vyžaduje roli Writer v obou prostorech " +
        "a oba prostory musí být dostupné přes MCP. Přesunutý podstrom převezme výchozí přístup cílového prostoru (vlastní režim přístupu a udělená " +
        "práva zanikají); proto je nutné acknowledgeAccessChange: true a výslovný rozsah includeSubtree (true = i všechny podstránky, " +
        "false = jen stránka bez podstránek). Neuložené živé úpravy kohokoli přesun zablokují. " + Immediate + " Přesun zpět je další volání tohoto nástroje " +
        "(přístupová práva se tím neobnoví). " + Versions + " Vyžaduje expectedSourceTreeVersion, expectedTargetTreeVersion a expectedVersion stránky.")]
    public Task<CallToolResult> MovePageToSpaceAsync(
        [Description("pageId přesouvané stránky.")] string pageId,
        [Description("spaceId cílového prostoru (atlas_list_spaces).")] string targetSpaceId,
        [Description("treeVersion zdrojového prostoru z posledního atlas_list_pages.")] long expectedSourceTreeVersion,
        [Description("treeVersion cílového prostoru z posledního atlas_list_pages.")] long expectedTargetTreeVersion,
        [Description("version stránky (expectedVersion) z atlas_list_pages.")] int expectedVersion,
        [Description("true = přesunout i všechny podstránky, false = jen tuto stránku (má-li podstránky, je to chyba).")] bool includeSubtree,
        [Description("true = potvrzuji, že přesunutý obsah převezme výchozí přístup cílového prostoru.")] bool acknowledgeAccessChange,
        [Description("Volitelně pageId rodiče v cílovém prostoru; bez něj kořen.")] string? parentId = null,
        [Description("Volitelně pageId sourozence v cílovém prostoru, před kterého se stránka zařadí; bez něj na konec.")] string? beforePageId = null,
        CancellationToken ct = default)
        => RunAsync("atlas_move_page_to_space", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            var target = McpArguments.RequiredId(targetSpaceId, "targetSpaceId");
            RequireTreeVersion(expectedSourceTreeVersion, "expectedSourceTreeVersion");
            RequireTreeVersion(expectedTargetTreeVersion, "expectedTargetTreeVersion");
            RequireVersion(expectedVersion);
            var parent = McpArguments.OptionalId(parentId, "parentId");
            var before = McpArguments.OptionalId(beforePageId, "beforePageId");
            if (!acknowledgeAccessChange)
            {
                return McpErrors.Error(400, "access_change_unacknowledged", "Přesun do jiného prostoru mění přístup: stránky převezmou výchozí přístup cílového prostoru " +
                    "a jejich vlastní režim přístupu a udělená práva zanikají. Nic se nezměnilo. Zopakujte s acknowledgeAccessChange: true, pokud to chcete.");
            }
            var change = await m_bus.InvokeAsync<CrossSpaceChange>(new MoveMcpPageToSpace(id,
                new MovePageSpaceRequest(target, parent, before, includeSubtree, expectedSourceTreeVersion, expectedTargetTreeVersion, expectedVersion, true)), ct);
            var text = new StringBuilder("Stránka přesunuta do jiného prostoru. " + Immediate + "\n");
            text.Append("- pageId: ").Append(id).Append('\n');
            text.Append("- zdrojový prostor ").Append(change.SourceSpaceId).Append(", treeVersion ").Append(Number(change.SourceVersion)).Append('\n');
            text.Append("- cílový prostor ").Append(change.TargetSpaceId).Append(", treeVersion ").Append(Number(change.TargetVersion)).Append('\n');
            text.Append("- přesunuté stránky: ").Append(change.AffectedIds.Count).Append('\n');
            text.Append("- odkaz pro člověka: ").Append(m_address.PageUrl(id)).Append('\n');
            var structured = new JsonObject
            {
                ["pageId"] = id,
                ["spaceId"] = change.TargetSpaceId,
                ["sourceSpaceId"] = change.SourceSpaceId,
                ["targetSpaceId"] = change.TargetSpaceId,
                ["sourceTreeVersion"] = change.SourceVersion,
                ["targetTreeVersion"] = change.TargetVersion,
                ["affectedIds"] = Ids(change.AffectedIds),
                ["url"] = m_address.PageUrl(id)
            };
            return Result(text.ToString(), structured);
        }, ct);

    [McpServerTool(Name = "atlas_trash_page", Title = "Přesunout stránku do koše", Destructive = true, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [Description("Přesune stránku do obnovitelného koše (nic se nemaže: obsah, historie, přílohy i publikace zůstávají a vrátí je atlas_restore_page, " +
        "kterou smí volat jen správce prostoru). Má-li stránka podstránky, je nutný výslovný rozsah includeSubtree: true (do koše jdou i ony); " +
        "bez něj je to chyba a nic se nezmění. Neuložené živé úpravy kohokoli v dotčených stránkách přesun zablokují (collaboration_unsaved). " +
        "Čtenáři stránku přestanou vidět okamžitě. " + Versions + " Vyžaduje expectedVersion stránky a expectedTreeVersion prostoru.")]
    public Task<CallToolResult> TrashPageAsync(
        [Description("pageId stránky.")] string pageId,
        [Description("version stránky (expectedVersion) z atlas_list_pages.")] int expectedVersion,
        [Description("expectedTreeVersion: treeVersion prostoru z posledního atlas_list_pages.")] long expectedTreeVersion,
        [Description("true = do koše i všechny podstránky, false = jen stránka bez podstránek.")] bool includeSubtree,
        CancellationToken ct = default)
        => RunAsync("atlas_trash_page", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            RequireVersion(expectedVersion);
            RequireTreeVersion(expectedTreeVersion, "expectedTreeVersion");
            var done = await m_bus.InvokeAsync<McpOrganizationResult>(new TrashMcpPage(id, new TrashPageRequest(includeSubtree, expectedTreeVersion, expectedVersion)), ct);
            return Changed("Stránka přesunuta do koše. Obnovíte ji přes atlas_list_trash a atlas_restore_page (jen správce prostoru).", id, done, null);
        }, ct);

    [McpServerTool(Name = "atlas_list_trash", Title = "Koš prostoru", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true)]
    [McpToolScope(ApiScopes.Read)]
    [Description("Vypíše stránky v koši prostoru (nejnovější první): pageId, název, původní rodič, čas přesunu do koše a trashBatchId stránek odstraněných " +
        "společně. Smí jen správce prostoru. Vrací treeVersion, který patří do atlas_restore_page jako expectedTreeVersion. " + McpText.TrustNotice)]
    public Task<CallToolResult> ListTrashAsync(
        [Description("spaceId z atlas_list_spaces.")] string spaceId,
        [Description("nextCursor z předchozího výsledku.")] string? cursor = null,
        CancellationToken ct = default)
        => RunAsync("atlas_list_trash", async () =>
        {
            var space = McpArguments.RequiredId(spaceId, "spaceId");
            var offset = McpCursor.Offset(cursor, "trash");
            var trash = await m_bus.InvokeAsync<PageTrashDto>(new ListMcpTrash(space, offset, TrashWindow), ct);
            var body = new StringBuilder();
            var items = new JsonArray();
            foreach (var page in trash.Items)
            {
                body.Append("- ").Append(McpText.Line(page.Title, 200)).Append(" (pageId: ").Append(page.Id);
                if (page.ParentId is { } parent)
                {
                    body.Append(", původní rodič: ").Append(parent);
                }
                body.Append(", v koši od ").Append(page.TrashedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))
                    .Append(", dávka: ").Append(page.TrashBatchId).Append(")\n");
                items.Add(new JsonObject
                {
                    ["pageId"] = page.Id, ["title"] = page.Title, ["parentId"] = page.ParentId, ["version"] = page.Version,
                    ["trashedAt"] = page.TrashedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), ["trashBatchId"] = page.TrashBatchId
                });
            }
            var nextCursor = trash.HasMore ? McpCursor.Create("trash", new JsonObject { ["o"] = offset + trash.Items.Count }) : null;
            var structured = new JsonObject { ["spaceId"] = space, ["treeVersion"] = trash.Version, ["items"] = items };
            if (nextCursor is not null)
            {
                structured["nextCursor"] = nextCursor;
            }
            return McpText.Result(McpText.Wrap($"Koš prostoru {space}: {trash.Items.Count} stránek na tomto výpisu, treeVersion {Number(trash.Version)}.",
                trash.Items.Count == 0 ? "Koš je prázdný." : body.ToString(),
                nextCursor is null ? null : $"Další stránky koše: cursor \"{nextCursor}\"."), structured);
        }, ct);

    [McpServerTool(Name = "atlas_restore_page", Title = "Obnovit stránku z koše", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [Description("Obnoví stránku z koše do jejího prostoru: pod parentId (stejný prostor, ne v koši), nebo na kořen (parentId vynechte). Obsah, historie, " +
        "přílohy a publikace se vrátí v původním stavu. Smí jen správce prostoru. includeSubtree: true obnoví i podstránky odstraněné společně s ní " +
        "(stejná dávka), false jen tuto stránku. expectedTreeVersion vezměte z atlas_list_trash. Čtenáři stránku uvidí okamžitě. Konflikt = " +
        "strom se mezitím změnil, nic se nezměnilo.")]
    public Task<CallToolResult> RestorePageAsync(
        [Description("pageId stránky z atlas_list_trash.")] string pageId,
        [Description("expectedTreeVersion: treeVersion z posledního atlas_list_trash.")] long expectedTreeVersion,
        [Description("true = obnovit i podstránky odstraněné společně s ní, false = jen tuto stránku.")] bool includeSubtree,
        [Description("pageId nové rodičovské stránky ve stejném prostoru; bez něj kořen prostoru.")] string? parentId = null,
        CancellationToken ct = default)
        => RunAsync("atlas_restore_page", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            RequireTreeVersion(expectedTreeVersion, "expectedTreeVersion");
            var parent = McpArguments.OptionalId(parentId, "parentId");
            var done = await m_bus.InvokeAsync<McpOrganizationResult>(new RestoreMcpPage(id, new UntrashPageRequest(parent, includeSubtree, expectedTreeVersion)), ct);
            return Changed("Stránka obnovena z koše.", id, done, done.Change.HasSiblingTitleCollision
                ? "Mezi sourozenci už existuje stránka se stejným názvem; případně jednu z nich přejmenujte (atlas_rename_page)."
                : null);
        }, ct);

    /// <summary>The result of a write in one space: the same facts as plain text and as structured content. No page text, so no wrapper.</summary>
    private CallToolResult Changed(string heading, Guid pageId, McpOrganizationResult done, string? note)
    {
        var url = m_address.PageUrl(pageId);
        var tree = Number(done.Change.Version);
        var text = new StringBuilder(heading).Append(' ').Append(Immediate).Append('\n');
        text.Append("- pageId: ").Append(pageId).Append('\n');
        text.Append("- spaceId: ").Append(done.SpaceId).Append('\n');
        text.Append("- treeVersion (expectedTreeVersion pro další změnu tohoto prostoru): ").Append(tree).Append('\n');
        text.Append("- dotčené stránky: ").Append(done.Change.AffectedIds.Count).Append('\n');
        text.Append("- odkaz pro člověka: ").Append(url).Append('\n');
        if (note is not null)
        {
            text.Append("Upozornění: ").Append(note).Append('\n');
        }
        if (done.Change.TitleOrderCancelled)
        {
            text.Append("Ruční přesun v úrovni řazené abecedně vypnul „Řadit abecedně“; znovu ho zapne atlas_sort_pages.\n");
        }
        var structured = new JsonObject
        {
            ["pageId"] = pageId,
            ["spaceId"] = done.SpaceId,
            ["treeVersion"] = done.Change.Version,
            ["expectedTreeVersion"] = done.Change.Version,
            ["affectedIds"] = Ids(done.Change.AffectedIds),
            ["siblingTitleCollision"] = done.Change.HasSiblingTitleCollision,
            ["titleOrderCancelled"] = done.Change.TitleOrderCancelled,
            ["url"] = url
        };
        return Result(text.ToString(), structured);
    }

    private static CallToolResult Result(string text, JsonObject structured) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        StructuredContent = JsonSerializer.SerializeToElement(structured)
    };

    private static JsonArray Ids(IReadOnlyList<Guid> ids) => new(ids.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

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

    private static void RequireVersion(int expectedVersion)
    {
        if (expectedVersion < 1)
        {
            throw new ApiException(400, "expectedVersion je povinné: version stránky z atlas_list_pages.", "invalid_argument");
        }
    }

    private static void RequireTreeVersion(long version, string name)
    {
        if (version < 1)
        {
            throw new ApiException(400, $"{name} je povinné: treeVersion prostoru z atlas_list_pages (nebo atlas_list_trash).", "invalid_argument");
        }
    }
}
