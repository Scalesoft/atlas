using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Mcp.Queries;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.PersonalApi;

namespace Atlas.Server.Features.Mcp.Tools;

/// <summary>
/// 3.6-I over MCP: the page's „Publikace“ (Manual, Automatic, Live) and the state of its publication. Setting it needs the write scope,
/// as any write. Not a Wolverine handler.
/// </summary>
[McpServerToolType]
public sealed class McpPublicationTools(IMessageBus bus, ILogger<McpPublicationTools> logger)
{
    private const string Modes = "Manual = čtenáři vidí verzi publikovanou ručně; Automatic = verze se publikuje sama po 30 minutách bez úprav; " +
        "Live = čtenáři čtou aktuální uložený stav živě (verze se publikují jako u Automatic). Stránka bez vlastní hodnoty dědí výchozí hodnotu prostoru. " +
        "Automatická publikace nikdy nezveřejní stránku, která ještě nebyla publikována, ani šablonu.";

    [McpServerTool(Name = "atlas_get_publication", Title = "Publikace stránky", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Jak se stránka publikuje (Ručně, Automaticky, Živě; zděděno z prostoru?) a poslední publikace. pageId.")]
    [Description("Vrátí publikaci stránky: vlastní hodnotu (mode, null = dědí spaceMode), platnou hodnotu a poslední publikaci (verze, kdy, kdo, " +
        "zda automaticky, zda je od ní něco uloženo). " + Modes + " " + McpText.TrustNotice)]
    public Task<CallToolResult> GetPublicationAsync(
        [Description("pageId stránky.")] string pageId,
        CancellationToken ct = default)
        => RunAsync("atlas_get_publication", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            return Result(id, await bus.InvokeAsync<PagePublicationState>(new GetMcpPublication(id), ct), null);
        }, ct);

    [McpServerTool(Name = "atlas_set_publication", Title = "Nastavit publikaci stránky", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Nastaví publikaci stránky: Manual, Automatic, Live, nebo inherit (dědit z prostoru). pageId, mode.")]
    [Description("Nastaví publikaci stránky (mode Manual/Ručně, Automatic/Automaticky, Live/Živě, nebo inherit pro výchozí hodnotu prostoru; velikost písmen a diakritika nerozhodují). " + Modes +
        " Smí každý s právem úprav stránky; přes MCP vyžaduje rozsah Zápis. Text stránky se nemění.")]
    public Task<CallToolResult> SetPublicationAsync(
        [Description("pageId stránky.")] string pageId,
        [Description("Manual (Ručně), Automatic (Automaticky), Live (Živě), nebo inherit (dědit z prostoru); na velikosti písmen a diakritice nezáleží.")] string? mode = null,
        CancellationToken ct = default)
        => RunAsync("atlas_set_publication", async () =>
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            if (mode is null)
            {
                return McpErrors.InvalidArgument("mode je povinné: Manual (Ručně), Automatic (Automaticky), Live (Živě), nebo inherit.");
            }
            // Parsed by the shared setting (case and Czech names), so REST and MCP accept the same values; "inherit" follows the space.
            var state = await bus.InvokeAsync<PagePublicationState>(new SetMcpPublication(id, new SetPagePublicationRequest(mode)), ct);
            return Result(id, state, "Publikace stránky nastavena.");
        }, ct);

    private static CallToolResult Result(Guid id, PagePublicationState state, string? heading)
    {
        var text = new StringBuilder();
        if (heading is not null) text.Append(heading).Append('\n');
        text.Append("- pageId: ").Append(id).Append('\n');
        // Automatic and Live apply only to a page published by hand once; before that it is private whatever the setting says.
        var neverPublished = state.PublishedVersion is null;
        var effective = neverPublished ? "Manual" : state.Effective;
        text.Append("- publikace: ").Append(effective).Append(state.Mode is null ? " (zděděno z prostoru)" : " (vlastní hodnota stránky)")
            .Append(neverPublished && state.Effective != "Manual" ? $"; nastavené {state.Effective} se uplatní až po první ruční publikaci" : "").Append('\n');
        text.Append("- poslední publikace: ").Append(state.PublishedVersion is { } version
            ? $"verze {version}, {state.PublishedAt:yyyy-MM-dd HH:mm} UTC{(state.PublishedAutomatically ? ", automaticky" : "")}"
            : "žádná (stránka je soukromá, dokud ji někdo nepublikuje ručně)").Append('\n');
        text.Append("- nepublikované změny: ").Append(state.UnpublishedChanges ? "ano" : "ne").Append('\n');
        var structured = new JsonObject
        {
            ["pageId"] = id, ["mode"] = state.Mode, ["spaceMode"] = state.SpaceMode, ["effective"] = effective, ["configured"] = state.Effective,
            ["publishedVersion"] = state.PublishedVersion, ["publishedAt"] = state.PublishedAt,
            ["publishedAutomatically"] = state.PublishedAutomatically, ["unpublishedChanges"] = state.UnpublishedChanges
        };
        return new() { Content = [new TextContentBlock { Text = text.ToString() }], StructuredContent = JsonSerializer.SerializeToElement(structured) };
    }

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
            logger.LogError("MCP tool {Tool} failed ({ErrorType})", tool, error.GetType().Name);
            return McpErrors.Error(500, "internal_error", "Nástroj se nepodařilo dokončit.");
        }
    }
}
