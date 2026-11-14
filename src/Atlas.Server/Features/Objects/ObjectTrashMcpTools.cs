using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Objects.Queries;
using Atlas.Server.Features.PersonalApi;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;

namespace Atlas.Server.Features.Objects;

/// <summary>
/// The trash of the objects the platform stores (contacts) over MCP (unified trash spec §2, rule 6): the list and the restore of the web and
/// /api/v1 as the token owner; the permanent delete stays the web's. Errors are the handlers' ApiExceptions, which the call filter turns into
/// tool errors and audits. Not a Wolverine handler: the class name does not end with "Handler".
/// </summary>
[McpServerToolType]
public sealed class ObjectTrashMcpTools(IMessageBus bus)
{
    [McpServerTool(Name = "atlas_list_object_trash", Title = "Koš objektů", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true)]
    [McpToolScope(ApiScopes.Read)]
    [Description("Vypíše objekty v koši (kontakty a další typy, které ukládá platforma; stránky a tabule mají atlas_list_trash), nejnovější první, 50 na " +
        "stránku: objectId, typ, rozsah, název, kdo a kdy je přesunul do koše. Jen ty, které smíte obnovit: objekt wiki ten, kdo smí objekty typu ve wiki " +
        "upravovat, objekt prostoru správce prostoru, soukromý objekt jeho vlastník. Obnoví je atlas_restore_object. " + McpText.TrustNotice)]
    public async Task<CallToolResult> ListAsync(
        [Description("Volitelně jen typ objektu, např. atlas.contact.")] string? type = null,
        [Description("Volitelně jen rozsah: wiki, space nebo private.")] string? scope = null,
        [Description("Volitelně jen objekty prostoru spaceId z atlas_list_spaces.")] string? spaceId = null,
        [Description("Stránka výpisu od 1.")] int page = 1,
        CancellationToken ct = default)
    {
        var trash = await bus.InvokeAsync<ObjectTrashList>(new ListObjectTrash(type, scope, McpArguments.OptionalId(spaceId, "spaceId"), page), ct);
        var body = new StringBuilder();
        var items = new JsonArray();
        foreach (var item in trash.Items)
        {
            var trashedAt = item.TrashedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            body.Append("- ").Append(McpText.Line(item.Title, 200)).Append(" (objectId: ").Append(item.Id).Append(", ").Append(item.TypeId).Append(", ").Append(item.Scope)
                .Append(", v koši od ").Append(item.TrashedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
            if (item.TrashedByName is { } by)
            {
                body.Append(", přesunul ").Append(McpText.Line(by, 100));
            }
            body.Append(")\n");
            items.Add(new JsonObject
            {
                ["objectId"] = item.Id, ["typeId"] = item.TypeId, ["scope"] = item.Scope, ["spaceId"] = item.SpaceId, ["title"] = item.Title, ["version"] = item.Version,
                ["trashedAt"] = trashedAt, ["trashedById"] = item.TrashedById, ["trashedByName"] = item.TrashedByName
            });
        }
        var more = trash.Total > trash.Page * trash.PageSize;
        return McpText.Result(McpText.Wrap($"Koš objektů: {trash.Items.Count} z {trash.Total}, stránka {trash.Page}.",
                trash.Items.Count == 0 ? "Koš je prázdný." : body.ToString(), more ? $"Další objekty koše: page {trash.Page + 1}." : null),
            new JsonObject { ["items"] = items, ["total"] = trash.Total, ["page"] = trash.Page, ["hasMore"] = more });
    }

    [McpServerTool(Name = "atlas_restore_object", Title = "Obnovit objekt z koše", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Obnoví objekt (kontakt) z koše tak, jak byl. objectId z atlas_list_object_trash.")]
    [Description("Obnoví objekt z koše (kontakt a další typy z atlas_list_object_trash) přesně tak, jak byl, s novou verzí; čtenáři ho uvidí hned. " +
        "Sloučený kontakt se vrací jako samostatný (odkazy ze sloučení mu zůstanou); má-li jeho e-mail nebo účet už jiný kontakt, obnova selže (409 contact_restore_duplicate). " +
        "Smí ten, kdo ho vidí v atlas_list_object_trash.")]
    public async Task<CallToolResult> RestoreAsync([Description("objectId z atlas_list_object_trash.")] string objectId, CancellationToken ct = default)
    {
        var id = McpArguments.RequiredId(objectId, "objectId");
        var restored = await bus.InvokeAsync<ObjectChangeResult>(new RestoreObject(id), ct);
        return McpText.Result($"Objekt obnoven z koše.\n- objectId: {id}\n- version: {restored.Version}\n",
            new JsonObject { ["objectId"] = id, ["version"] = restored.Version });
    }
}
