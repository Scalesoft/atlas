using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.TreeReferences.Commands;
using Atlas.Server.Features.TreeReferences.Contracts;

namespace Atlas.Server.Features.TreeReferences;

/// <summary>
/// Reference nodes of the space tree over MCP (AI parity, spec 2026-10-27 §8): the commands of the web and /api/v1 as the token owner, G5
/// checked in the handler under its locks; errors are the handlers' ApiExceptions, which the call filter turns into tool errors and audits
/// (write scope: the tree changes at once, as a page move). Not a Wolverine handler: the class name does not end with "Handler".
/// </summary>
[McpServerToolType]
public sealed class TreeReferenceMcpTools(IMessageBus bus)
{
    private const string Immediate = "Strom se změní čtenářům hned; jde to vrátit (atlas_remove_tree_reference, další přesun).";

    [McpServerTool(Name = "atlas_add_tree_reference", Title = "Přidat odkaz do stromu", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Přidá do stromu prostoru odkazový uzel na objekt (stránku, sekci, tabuli, kontakt). spaceId, targetId, anchor?, parentId?, beforeId?.")]
    [Description("Přidá do stromu prostoru odkazový uzel: umístění libovolného objektu, který smíte číst (stránka, sekce stránky přes anchor = headingId, " +
        "blok požadavku přes anchor = blockId, tabule, kontakt), i z jiného prostoru nebo z celé wiki. Není to kopie; uzel otevře cíl. Pod parentId " +
        "(stránka prostoru), bez něj na kořen; před beforeId (stránka, tabule nebo uzel téže úrovně), bez něj na konec. Vyžaduje roli Writer v prostoru. " + Immediate)]
    public async Task<CallToolResult> AddAsync(
        [Description("spaceId prostoru, jehož strom se mění.")] string spaceId,
        [Description("id cílového objektu (pageId, boardId, id kontaktu…).")] string targetId,
        [Description("Volitelně místo v cíli: headingId nadpisu nebo blockId bloku.")] string? anchor = null,
        [Description("Volitelně pageId rodičovské stránky v tomto prostoru; bez něj kořen.")] string? parentId = null,
        [Description("Volitelně id sourozence, před kterého se uzel zařadí; bez něj na konec.")] string? beforeId = null,
        CancellationToken ct = default)
    {
        var space = McpArguments.RequiredId(spaceId, "spaceId");
        var node = await bus.InvokeAsync<PageTreeItem>(new AddTreeReference(space, new AddTreeReferenceRequest(McpArguments.RequiredId(targetId, "targetId"),
            anchor, McpArguments.OptionalId(parentId, "parentId"), McpArguments.OptionalId(beforeId, "beforeId"))), ct);
        return McpText.Result($"Odkaz přidán do stromu. {Immediate}\n- referenceId: {node.Id}\n- cíl: {McpText.Line(node.Title, 200)} ({node.TargetTypeId}, {node.TargetId})\n",
            new JsonObject
            {
                ["referenceId"] = node.Id, ["spaceId"] = space, ["parentId"] = node.ParentId, ["targetId"] = node.TargetId, ["targetTypeId"] = node.TargetTypeId,
                ["anchor"] = node.Anchor, ["title"] = node.Title
            });
    }

    [McpServerTool(Name = "atlas_remove_tree_reference", Title = "Odebrat odkaz ze stromu", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Odebere odkazový uzel ze stromu; cíl zůstane beze změny. referenceId.")]
    [Description("Odebere odkazový uzel ze stromu prostoru. Zruší jen odkaz, cílový objekt zůstane beze změny. Vyžaduje roli Writer v prostoru.")]
    public async Task<CallToolResult> RemoveAsync([Description("referenceId odkazového uzlu.")] string referenceId, CancellationToken ct = default)
    {
        var id = McpArguments.RequiredId(referenceId, "referenceId");
        await bus.InvokeAsync(new RemoveTreeReference(id), ct);
        return McpText.Result($"Odkaz odebrán ze stromu.\n- referenceId: {id}\n", new JsonObject { ["referenceId"] = id });
    }

    [McpServerTool(Name = "atlas_move_tree_reference", Title = "Přesunout odkaz ve stromu", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Přesune odkazový uzel pod parentId (bez něj kořen) před beforeId. referenceId, expectedTreeVersion.")]
    [Description("Přesune odkazový uzel v jeho prostoru pod stránku parentId (bez něj na kořen) a před sourozence beforeId (bez něj na konec), " +
        "jako atlas_move_page. expectedTreeVersion vezměte z atlas_list_pages; při konfliktu se nic nezmění. " + Immediate)]
    public async Task<CallToolResult> MoveAsync(
        [Description("referenceId odkazového uzlu.")] string referenceId,
        [Description("expectedTreeVersion: treeVersion prostoru z posledního atlas_list_pages.")] long expectedTreeVersion,
        [Description("Volitelně pageId nové rodičovské stránky; bez něj kořen.")] string? parentId = null,
        [Description("Volitelně id sourozence, před kterého se uzel zařadí; bez něj na konec.")] string? beforeId = null,
        CancellationToken ct = default)
    {
        var id = McpArguments.RequiredId(referenceId, "referenceId");
        var change = await bus.InvokeAsync<OrganizationChange>(new MoveTreeReference(id,
            new MovePageRequest(McpArguments.OptionalId(parentId, "parentId"), McpArguments.OptionalId(beforeId, "beforeId"), expectedTreeVersion)), ct);
        return McpText.Result($"Odkaz přesunut. {Immediate}\n- referenceId: {id}\n- treeVersion: {change.Version}\n",
            new JsonObject { ["referenceId"] = id, ["treeVersion"] = change.Version, ["expectedTreeVersion"] = change.Version });
    }
}
