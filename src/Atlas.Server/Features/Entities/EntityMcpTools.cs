using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;
using Atlas.Server.Features.Entities.Queries;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PersonalApi;

namespace Atlas.Server.Features.Entities;

/// <summary>
/// <c>list_entities</c> over MCP (plan §14.4): the web's entity query as the token owner; rights and G5 are the handler's
/// (ListEntitiesHandler on the MCP channel leaves spaces switched out of AI out). Not a Wolverine handler.
/// </summary>
[McpServerToolType]
public sealed class EntityMcpTools(IMessageBus bus)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "atlas_list_entities", Title = "Entity ve wiki", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Přesný seznam entit (kind contact: kontakty) ve wiki, prostoru, podstromu nebo stránce, sloučených podle e-mailu či jména, se zdroji. kind, scope, id, q, page.")]
    [Description("Vypíše entity daného druhu (zatím kind contact: kontakty z vizitek a zmínek ve stránkách a z adresáře) ze stránek a adresáře, které smí " +
        "vlastník tokenu číst, sloučené podle identity (e-mail, účet, id v adresáři, kontakt adresáře, jinak jméno s organizací). U každé entity sloučené " +
        "atributy a zdroje: stránka (čtenář publikovanou verzi, autor pracovní) s kotvou nadpisu, nebo kontakt adresáře. scope instance (celá wiki, výchozí), " +
        "space, subtree (stránka a vše pod ní) nebo resource (jedna stránka či kontakt) s id; q slova v libovolném poli; po 50 na stránku (page). " +
        "U entity je prvních 5 zdrojů a jejich počet (sourceCount); identity (identityKey entity) vypíše jen ji, se všemi zdroji. " +
        "„Všechny kontakty v prostoru“ je jedno volání. contactId entity je kontakt adresáře, na který odkazuje vizitka (objectId) i karta tabule; bez něj osobu propojí atlas_upsert_contact. " + McpText.TrustNotice)]
    public async Task<CallToolResult> ListAsync(
        [Description("Druh entity: contact.")] string kind = EntityKinds.Contact,
        [Description("instance, space, subtree nebo resource.")] string scope = "instance",
        [Description("spaceId pro space, pageId pro subtree, id stránky nebo kontaktu pro resource.")] string? id = null,
        [Description("Volitelně slova, která entita má v některém poli.")] string? q = null,
        [Description("Stránka výsledku od 1.")] int page = 1,
        [Description("Volitelně identityKey jedné entity: jen ona, se všemi zdroji.")] string? identity = null,
        CancellationToken ct = default)
    {
        var list = await bus.InvokeAsync<EntityList>(new ListEntities(kind, scope, McpArguments.OptionalId(id, "id"), q, page, Identity: identity), ct);
        var body = new StringBuilder();
        foreach (var item in list.Items)
        {
            var a = item.Attributes;
            body.Append("- ").Append(McpText.Line(a["name"]?.GetValue<string>(), 200));
            foreach (var key in new[] { "title", "organization" })
            {
                if (a[key] is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    body.Append(", ").Append(McpText.Line(text, 200));
                }
            }
            if (a["emails"] is JsonArray emails && emails.Count > 0)
            {
                body.Append(", ").Append(McpText.Line(string.Join(" ", emails.Select(email => email?.ToString())), 300));
            }
            if (a["aliases"] is JsonArray aliases && aliases.Count > 0)
            {
                body.Append(", zkratky: ").Append(McpText.Line(string.Join(", ", aliases.Select(alias => alias?.ToString())), 300));
            }
            if (item.ContactId is { } contactId)
            {
                body.Append(" · contactId ").Append(contactId);
            }
            body.Append(" — zdroje: ").Append(string.Join("; ", item.Sources.Select(source =>
                McpText.Line(source.Title, 120) + " (" + source.ResourceType + " " + source.ResourceId + (source.Anchor is { } anchor ? "#" + anchor : "") + ")")));
            if (item.SourceCount > item.Sources.Count)
            {
                body.Append("; a dalších ").Append(item.SourceCount - item.Sources.Count).Append(" (identity ").Append(McpText.Line(item.IdentityKey, 300)).Append(')');
            }
            body.Append('\n');
        }
        var more = list.Page * list.PageSize < list.Total;
        return McpText.Result(McpText.Wrap($"Entity {kind} v rozsahu {scope}: {list.Total}, na stránce {list.Page} {list.Items.Count}.",
                list.Items.Count == 0 ? "Nic." : body.ToString(), more ? $"Další: page {list.Page + 1}." : null),
            JsonSerializer.SerializeToNode(list, Json)!.AsObject());
    }
}
