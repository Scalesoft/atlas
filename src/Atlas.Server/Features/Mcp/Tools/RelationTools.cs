using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;
using Atlas.Server.Features.Assistant.Agent;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Queries;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Queries;

namespace Atlas.Server.Features.Mcp.Tools;

/// <summary>
/// The space's contents and an object's relations over MCP (spec 2026-10-27 §8): the web's handlers, narrowed by G5. A space switched
/// out of AI is 404 for the contents; in relations its objects become nodes the token may not read. Errors are the handlers'
/// ApiExceptions, which the call filter turns into tool errors. Not a Wolverine handler.
/// </summary>
[McpServerToolType]
public sealed class McpRelationTools(IMessageBus bus)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "atlas_list_space_contents", Title = "Obsah prostoru", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Vše v prostoru (stránky, tabule, kontakty) s počty použití, odkazů a komentářů; filtr type, relation (unused, noIncoming, broken, attached, mostUsed), q. spaceId, cursor.")]
    [Description("Vypíše obsah prostoru, který smí vlastník tokenu číst: stránky, tabule, objekty prostoru a objekty celé wiki v prostoru použité (scope wiki). " +
        "U každého id, typ, název, cesta ve stromu, hostitel připojené tabule (hostId), počet použití (karty na tabulích, zmínky ve stránkách), odkazy sem a ven, " +
        "komentáře a čas změny; souhrn počtů po typech a vztazích. relation: unused (nikde nepoužité), noIncoming (nic na ně nevede), broken (rozbité odkazy " +
        "do koše), attached (tabule patřící stránce), mostUsed (nejpoužívanější). Po 50 položkách pokračování přes nextCursor. " + McpText.TrustNotice)]
    public async Task<CallToolResult> ListSpaceContentsAsync(
        [Description("spaceId z atlas_list_spaces.")] string spaceId,
        [Description("Volitelně jen typ objektu, např. atlas.page, atlas.board, atlas.contact.")] string? type = null,
        [Description("Volitelně vztah: unused, noIncoming, broken, attached nebo mostUsed.")] string? relation = null,
        [Description("Volitelně slova z názvu.")] string? q = null,
        [Description("nextCursor z předchozího výsledku.")] string? cursor = null,
        CancellationToken ct = default)
    {
        var space = McpArguments.RequiredId(spaceId, "spaceId");
        if ((await bus.InvokeAsync<McpScope>(new CheckMcpScope([], [space]), ct)).ExcludedSpaces.Contains(space))
        {
            throw new ApiException(404, McpVisibility.MissingSpace);
        }
        var contents = await bus.InvokeAsync<SpaceContents>(new ListSpaceContents(space, type, relation, q, null, cursor), ct);
        var body = new StringBuilder();
        foreach (var row in contents.Items)
        {
            body.Append("- ").Append(McpText.Line(row.Title, 200)).Append(" (").Append(row.TypeId).Append(", id ").Append(row.Id);
            if (row.Path.Length > 0)
            {
                body.Append(", cesta ").Append(McpText.Line(row.Path, 300));
            }
            if (row.Scope != "space")
            {
                body.Append(", rozsah ").Append(row.Scope);
            }
            if (row.HostId is { } host)
            {
                body.Append(", patří ke stránce ").Append(host);
            }
            body.Append(", použito na ").Append(row.UsedCount).Append(" místech").Append(", odkazy sem ").Append(row.LinksIn).Append(" a ven ").Append(row.LinksOut)
                .Append(", komentáře ").Append(row.Comments).Append(")\n");
        }
        var summary = contents.Summary.Relations;
        var facts = $"Obsah prostoru {space}: {contents.Items.Count} položek na této stránce. Nepoužité {summary.Unused}, bez odkazů sem {summary.NoIncoming}, " +
            $"s rozbitým odkazem {summary.Broken}, připojené {summary.Attached}.";
        var structured = JsonSerializer.SerializeToNode(contents, Json)!.AsObject();
        structured["spaceId"] = space;
        return McpText.Result(McpText.Wrap(facts, contents.Items.Count == 0 ? "Nic." : body.ToString(),
            contents.NextCursor is { } next ? "Další položky: cursor " + next : null), structured);
    }

    [McpServerTool(Name = "atlas_get_relations", Title = "Vazby objektu", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Vazby objektu a kde je použit: odkazy, umístění na tabulích, připojení, zmínky; uzly a hrany s důvodem (blok, karta, úryvek). objectId, depth 1|2, kinds, includeDrafts.")]
    [Description("Vrátí vazby objektu (stránky, tabule, kontaktu) jako uzly a hrany: link (stránka odkazuje na stránku), placement (objekt je kartou na tabuli, " +
        "count karet), attached (tabule patří stránce), mention (stránka zmiňuje kontakt), reference (odkazový uzel ve stromu pod stránkou ukazuje na objekt). " +
        "U míst, kde je objekt použit, hrana nese důvod: partId (karta, odkazový uzel nebo blok stránky) a excerpt (úryvek textu bloku). includeDrafts přidá " +
        "použití jen v pracovní verzi stránky (draft true), ale jen u stránek, které smí vlastník tokenu upravovat. depth 2 přidá vazby čitelných sousedů. " +
        "Uzel, který vlastník tokenu číst nesmí (nebo je v koši), má readable false a nic dalšího; hidden je počet vazeb objektu, které nevidí. " +
        "atlas_get_backlinks zůstává pro odkazy na stránku. Vazby vznikají z publikovaného obsahu až několik sekund po publikaci (projekce na pozadí): čerstvě publikovaný odkaz se tu může objevit se zpožděním. " + McpText.TrustNotice)]
    public async Task<CallToolResult> GetRelationsAsync(
        [Description("objectId (pageId, id tabule nebo kontaktu).")] string objectId,
        [Description("Hloubka 1 (výchozí) nebo 2.")] int depth = 1,
        [Description("Volitelně druhy oddělené čárkou: link, placement, attached, mention, reference.")] string? kinds = null,
        [Description("true: i použití jen v konceptech (pracovních verzích) stránek.")] bool includeDrafts = false,
        CancellationToken ct = default)
    {
        var id = McpArguments.RequiredId(objectId, "objectId");
        // G5 is the handler's (RelationNodes on the MCP channel): an object of an excluded space is a lock, and depth 2 stops there.
        var relations = await bus.InvokeAsync<ObjectRelations>(new GetObjectRelations(id, depth, kinds, includeDrafts, Reasons: true), ct);
        var titles = relations.Nodes.ToDictionary(node => node.Id, node => node.Readable ? McpText.Line(node.Title, 200) + " (" + node.TypeId + ", id " + node.Id + ")" : "nečitelný objekt (id " + node.Id + ")");
        var body = new StringBuilder();
        foreach (var edge in relations.Edges)
        {
            body.Append("- ").Append(titles[edge.SourceId]).Append(" —").Append(edge.Kind).Append(edge.Count > 1 ? $" ×{edge.Count}" : "").Append("→ ")
                .Append(titles[edge.TargetId]).Append(edge.Card ? " (vizitka)" : "").Append(edge.Draft ? " (jen v konceptu)" : "");
            if (edge.Excerpt is { } excerpt)
            {
                body.Append(": „").Append(McpText.Line(excerpt, 200)).Append('“');
            }
            body.Append('\n');
        }
        var structured = JsonSerializer.SerializeToNode(relations, Json)!.AsObject();
        structured["objectId"] = id;
        var hidden = relations.Hidden > 0 ? $" Skrytých (nečitelných) vazeb: {relations.Hidden}." : "";
        return McpText.Result(McpText.Wrap($"Vazby objektu {titles[id]}: {relations.Edges.Count}.{hidden}", relations.Edges.Count == 0 ? "Žádné." : body.ToString()), structured);
    }

    [McpServerTool(Name = "atlas_list_usage", Title = "Použití v prostoru", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Inventář prostoru: komponenty podle typu s počtem použití a příznakem nepoužité; filtr type, unused. spaceId, cursor.")]
    [Description("Vypíše komponenty prostoru (stránky, tabule, kontakty a další objekty, i objekty wiki v prostoru použité) podle typu s počtem míst, kde jsou " +
        "použité a vlastník tokenu je vidí (uses, včetně konceptů stránek), a příznakem unused: objekt mimo strom, který nic nepoužívá " +
        "ani skrytě; takové objekty může úklid sirotků po nastavené době přesunout do koše. unused true vypíše jen nepoužité. Kde přesně je objekt použitý, " +
        "řekne atlas_get_relations. Po 50 položkách pokračování přes nextCursor. " + McpText.TrustNotice)]
    public async Task<CallToolResult> ListUsageAsync(
        [Description("spaceId z atlas_list_spaces.")] string spaceId,
        [Description("Volitelně jen typ objektu, např. atlas.contact, atlas.board.")] string? type = null,
        [Description("true: jen nepoužité objekty.")] bool unused = false,
        [Description("nextCursor z předchozího výsledku.")] string? cursor = null,
        CancellationToken ct = default)
    {
        var space = McpArguments.RequiredId(spaceId, "spaceId");
        if ((await bus.InvokeAsync<McpScope>(new CheckMcpScope([], [space]), ct)).ExcludedSpaces.Contains(space))
        {
            throw new ApiException(404, McpVisibility.MissingSpace);
        }
        var inventory = await bus.InvokeAsync<SpaceUsage>(new ListSpaceUsage(space, type, unused, cursor), ct);
        var body = new StringBuilder();
        foreach (var row in inventory.Items)
        {
            body.Append("- ").Append(McpText.Line(row.Title, 200)).Append(" (").Append(row.TypeId).Append(", id ").Append(row.Id)
                .Append(", použito na ").Append(row.Uses).Append(" místech").Append(row.Unused ? ", nepoužité" : "").Append(")\n");
        }
        var structured = JsonSerializer.SerializeToNode(inventory, Json)!.AsObject();
        structured["spaceId"] = space;
        return McpText.Result(McpText.Wrap($"Použití v prostoru {space}: {inventory.Items.Count} položek na této stránce, nepoužitých celkem {inventory.Unused}.",
            inventory.Items.Count == 0 ? "Nic." : body.ToString(), inventory.NextCursor is { } next ? "Další položky: cursor " + next : null), structured);
    }
}
