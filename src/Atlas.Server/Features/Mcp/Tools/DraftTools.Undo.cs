using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.PersonalApi;

namespace Atlas.Server.Features.Mcp.Tools;

/// <summary>Undo of one revision's change as a whole (3.6-H, AI parity with the history's „Vrátit tuto změnu“): <see cref="UndoRevisionChange"/>.</summary>
public sealed partial class McpDraftTools
{
    [McpServerTool(Name = "atlas_undo_change", Title = "Vrátit jednu změnu stránky", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Vrátí jednu změnu stránky (verzi z atlas_get_history, třeba svůj zápis) jako celek; ostatní změny zůstanou. pageId, revisionId, expectedVersion.")]
    [Description("Vrátí změnu, kterou udělala jedna verze stránky (revisionId z atlas_get_history), jako celek: vrátí se jen bloky, které ta verze " +
        "změnila, co kdo napsal jinde, zůstane. Hodí se k vrácení vlastního zápisu. Když se ta místa od té doby změnila, odmítne to " +
        "(change_not_revertible); pak lze obnovit celou verzi (atlas_restore_version). " + WorkingFirst + " " + DraftNotice)]
    public Task<CallToolResult> UndoChangeAsync(
        [Description("pageId stránky.")] string pageId,
        [Description("revisionId verze, jejíž změnu vrátit (z atlas_get_history).")] string revisionId,
        [Description("expectedVersion z posledního čtení pracovní verze.")] int expectedVersion,
        CancellationToken ct = default)
        => RunAsync("atlas_undo_change", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            var revision = McpArguments.RequiredId(revisionId, "revisionId");
            RequireVersion(expectedVersion);
            var page = await m_bus.InvokeAsync<PageDto>(new UndoRevisionChange(id, revision, expectedVersion), ct);
            return Written(page.Version == expectedVersion ? "Obsah je beze změny." : "Změna vrácena.",
                new McpWriteResult(page.Id, page.SpaceId, page.Version, []), []);
        }, ct);
}
