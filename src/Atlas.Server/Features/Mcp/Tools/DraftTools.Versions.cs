using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.PageHistory.Commands;
using Atlas.Server.Features.PageHistory.Contracts;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.PersonalApi;

namespace Atlas.Server.Features.Mcp.Tools;

/// <summary>The history's „Obnovit tuto verzi“ and „Uložit verzi“ (3.6-C) over MCP, as /api/v1 has them: <see cref="RestorePage"/> and
/// <see cref="NameVersion"/>.</summary>
public sealed partial class McpDraftTools
{
    [McpServerTool(Name = "atlas_restore_version", Title = "Obnovit verzi stránky", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Obnoví celou starší verzi stránky (revisionId z atlas_get_history) jako novou pracovní verzi; nic se nemaže. pageId, revisionId, expectedVersion.")]
    [Description("Obnoví celý obsah starší verze stránky (revisionId z atlas_get_history) jako novou pracovní verzi; historie zůstane celá, obnovu " +
        "lze vrátit obnovou jiné verze. Obnova publikované verze (v atlas_get_history označená jako publikovaná) zahodí nepublikované změny: " +
        "stránka je pak znovu publikovaná beze změn a zahozený stav zůstane v historii. Jen jednu změnu vrátí atlas_undo_change. Má-li stránku někdo otevřenou s neuloženými úpravami, obnova se " +
        "odmítne (collaboration_unsaved), aby se nic nepřepsalo. Nejdřív si přečtěte pracovní verzi (atlas_get_page s version: \"working\") a " +
        "použijte její expectedVersion. " + DraftNotice)]
    public Task<CallToolResult> RestoreVersionAsync(
        [Description("pageId stránky.")] string pageId,
        [Description("revisionId verze, kterou obnovit (z atlas_get_history).")] string revisionId,
        [Description("expectedVersion z posledního čtení pracovní verze.")] int expectedVersion,
        CancellationToken ct = default)
        => RunAsync("atlas_restore_version", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            var revision = McpArguments.RequiredId(revisionId, "revisionId");
            RequireVersion(expectedVersion);
            var page = await m_bus.InvokeAsync<PageDto>(new RestorePage(id, new RestorePageRequest(revision, expectedVersion)), ct);
            return Written("Verze obnovena.", new McpWriteResult(page.Id, page.SpaceId, page.Version, []), []);
        }, ct);

    [McpServerTool(Name = "atlas_name_version", Title = "Uložit verzi stránky", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Uloží aktuální stav stránky jako pojmenovanou verzi historie („Uložit verzi“); obsah se nemění. pageId, expectedVersion, volitelně label.")]
    [Description("Uloží aktuální pracovní verzi stránky jako pojmenovaný bod historie („Uložit verzi“): verze se uzavře, další změna začne novou. " +
        "Obsah se nemění. label je volitelný popis verze (jeden řádek, nejvýše 500 znaků). expectedVersion z posledního čtení pracovní verze.")]
    public Task<CallToolResult> NameVersionAsync(
        [Description("pageId stránky.")] string pageId,
        [Description("expectedVersion z posledního čtení pracovní verze.")] int expectedVersion,
        [Description("Popis verze (jeden řádek, nejvýše 500 znaků).")] string? label = null,
        CancellationToken ct = default)
        => RunAsync("atlas_name_version", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            RequireVersion(expectedVersion);
            var named = await m_bus.InvokeAsync<NamedVersion>(new NameVersion(id, expectedVersion, label), ct);
            return McpText.Result($"Verze uložena.\n- pageId: {id}\n- revisionId: {named.RevisionId}\n- verze: {named.Version}\n",
                new System.Text.Json.Nodes.JsonObject { ["pageId"] = id, ["revisionId"] = named.RevisionId, ["version"] = named.Version });
        }, ct);
}
