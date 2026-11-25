using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Queries;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;

namespace Atlas.Server.Features.PersonalLibrary;

/// <summary>
/// Co se změnilo over MCP (AI parity): the list of the home dashboard and GET /api/v1/me/changes, as the token owner sees it, and on MCP
/// without the spaces out of AI (G5, decided by the handler's RelationNodes). Not a Wolverine handler.
/// </summary>
[McpServerToolType]
public sealed class RecentChangesMcpTools(IMessageBus bus)
{
    [McpServerTool(Name = "atlas_list_recent_changes", Title = "Co se změnilo", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true)]
    [McpToolScope(ApiScopes.Read)]
    [Description("Vypíše stránky změněné za posledních 30 dní, které vlastník tokenu smí číst, nejnovější změna první, jedna řádka na stránku: pageId, název, " +
        "prostor, kdo a kdy, kanál (web, mcp, api, assistant), zda šlo o změnu s AI a kolik dalších verzí. Neuveřejněné změny jen u stránek, které smí upravovat. " +
        "S authorId jen změny jednoho člověka (co dělal a kdy), bez omezení na 30 dní, a na první stránce i jeho rok po dnech a prostorech (days), se spaceId jen v jednom prostoru. Historii stránky čte atlas_get_history. " + McpText.TrustNotice)]
    public async Task<CallToolResult> ListAsync(
        [Description("Volitelně all (výchozí), ai (jen změny s AI) nebo mine (stránky, které vlastní, upravoval nebo má v oblíbených).")] string? filter = null,
        [Description("Volitelně next z předchozí stránky výpisu.")] string? cursor = null,
        [Description("Volitelně id účtu: jen jeho změny (directoryId kontaktu z atlas_get_contact, authorId z tohoto výpisu).")] string? authorId = null,
        [Description("Volitelně spaceId z atlas_list_spaces: jen stránky toho prostoru.")] string? spaceId = null,
        CancellationToken ct = default)
    {
        var author = McpArguments.OptionalId(authorId, "authorId");
        var list = await bus.InvokeAsync<RecentChangeList>(new ListRecentChanges(filter ?? "all", cursor, 20, author, McpArguments.OptionalId(spaceId, "spaceId")), ct);
        // A person's year by space (their profile's calendar), on the first page of their changes.
        var year = author is { } person && cursor is null ? await bus.InvokeAsync<PersonActivity>(new GetPersonActivity(person), ct) : null;
        var body = new StringBuilder();
        var items = new JsonArray();
        foreach (var item in list.Items)
        {
            var at = item.At.ToString("O", CultureInfo.InvariantCulture);
            body.Append("- ").Append(McpText.Line(item.Title, 200)).Append(" (pageId: ").Append(item.PageId);
            if (item.SpaceName is { } space)
            {
                body.Append(", prostor ").Append(McpText.Line(space, 100));
            }
            body.Append(", ").Append(McpText.Line(item.AuthorName, 100)).Append(", ").Append(item.Channel ?? "web").Append(item.IsAi ? ", AI" : "")
                .Append(item.ApiToken is { } token ? $", token {McpText.Line(token.Name, 100)}" : "")
                .Append(", v").Append(item.Version).Append(", ").Append(item.At.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))
                .Append(item.FurtherChanges > 0 ? $", +{item.FurtherChanges} dalších verzí" : "").Append(")\n");
            items.Add(new JsonObject
            {
                ["pageId"] = item.PageId, ["title"] = item.Title, ["spaceId"] = item.SpaceId, ["spaceName"] = item.SpaceName, ["revisionId"] = item.RevisionId,
                ["version"] = item.Version, ["at"] = at, ["authorId"] = item.AuthorId, ["authorName"] = item.AuthorName, ["channel"] = item.Channel,
                ["isAi"] = item.IsAi, ["apiToken"] = item.ApiToken?.Name, ["furtherChanges"] = item.FurtherChanges
            });
        }
        if (year is not null)
        {
            body.Append("Za posledních 365 dní po prostorech: ").AppendJoin(", ", year.Days.GroupBy(day => day.SpaceName ?? "bez prostoru")
                .Select(space => $"{McpText.Line(space.Key, 100)} {space.Sum(day => day.Changes)}")).Append(".\n");
        }
        var structured = new JsonObject { ["items"] = items, ["next"] = list.Next };
        if (year is not null)
        {
            structured["days"] = new JsonArray(year.Days.Select(day => (JsonNode)new JsonObject
                { ["day"] = day.Day, ["spaceId"] = day.SpaceId, ["spaceName"] = day.SpaceName, ["changes"] = day.Changes }).ToArray());
        }
        return McpText.Result(McpText.Wrap($"Co se změnilo: {list.Items.Count} stránek.",
                list.Items.Count == 0 && year is null ? "Žádné změny." : body.ToString(), list.Next is { } next ? $"Další stránky: cursor {next}." : null),
            structured);
    }
}
