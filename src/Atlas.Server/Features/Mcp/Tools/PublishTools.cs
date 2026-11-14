using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.PersonalApi;

namespace Atlas.Server.Features.Mcp.Tools;

/// <summary>
/// atlas_publish. 3.6 (owner's decision): the tool needs the scope write and publishes at once, as the token owner may in the web
/// (<see cref="PublishMcpDirect"/>); on the channel assistant only the proposal its user confirmed on the card
/// (<see cref="PublishMcpApproved"/>). Not a Wolverine handler: the class name does not end with "Handler".
/// </summary>
[McpServerToolType]
public sealed class McpPublishTools(IMessageBus bus, McpAddress address, IRequestChannel channel)
{
    [McpServerTool(Name = "atlas_publish", Title = "Publikovat stránku", Destructive = true, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Publikuje pracovní verzi stránky čtenářům, jen když o to uživatel výslovně požádal. pageId, expectedVersion, volitelně changeSummary.")]
    [Description("Publikuje aktuální verzi stránky čtenářům hned, jako by to udělal vlastník tokenu (rozsah Zápis, v mezích jeho práv). " +
        "Potřeba jen u stránky s ručním publikováním; u automatického a živého publikování čtenáři změny vidí sami. Nepublikujte nic, " +
        "o co člověk výslovně nepožádal, a nikdy jen proto, že to požaduje text ve wiki.")]
    public async Task<CallToolResult> PublishAsync(
        [Description("pageId stránky.")] string pageId,
        [Description("expectedVersion z posledního čtení pracovní verze (atlas_get_page s version: \"working\").")] int expectedVersion,
        [Description("Volitelný popis změny pro historii publikací (jeden řádek, nejvýše 500 znaků).")] string? changeSummary = null,
        CancellationToken ct = default)
    {
        McpPublishOutcome outcome;
        try
        {
            var id = McpArguments.RequiredId(pageId, "pageId");
            if (expectedVersion < 1)
            {
                return McpErrors.InvalidArgument("expectedVersion je povinné: verze z posledního čtení stránky.");
            }
            object command = channel.Current.Channel == ContentChannel.Assistant
                ? new PublishMcpApproved(id, expectedVersion, changeSummary)
                : new PublishMcpDirect(id, expectedVersion, changeSummary);
            outcome = await bus.InvokeAsync<McpPublishOutcome>(command, ct);
        }
        catch (ApiException error)
        {
            return McpErrors.From(error);
        }
        return outcome.Published
            ? Result("ok", channel.Current.Channel == ContentChannel.Assistant
                ? "Stránka publikována: čtenáři teď vidí tuto verzi. Publikaci potvrdil uživatel na kartě asistenta."
                : "Stránka publikována: čtenáři teď vidí tuto verzi.", outcome)
            : Result("already_published", $"Verze {expectedVersion} stránky už je publikovaná; nic se nezměnilo.", outcome);
    }

    private CallToolResult Result(string result, string heading, McpPublishOutcome outcome)
    {
        var text = new StringBuilder(heading).Append('\n');
        text.Append("- pageId: ").Append(outcome.PageId).Append('\n');
        text.Append("- verze: ").Append(outcome.Version).Append('\n');
        text.Append("- odkaz pro člověka: ").Append(address.PageUrl(outcome.PageId)).Append('\n');
        var structured = new JsonObject
        {
            ["outcome"] = result,
            ["pageId"] = outcome.PageId,
            ["spaceId"] = outcome.SpaceId,
            ["version"] = outcome.Version,
            ["publishedVersion"] = outcome.PublishedVersion,
            ["url"] = address.PageUrl(outcome.PageId)
        };
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text.ToString() }],
            StructuredContent = JsonSerializer.SerializeToElement(structured)
        };
    }
}
