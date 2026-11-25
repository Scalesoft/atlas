using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Notifications.Commands;
using Atlas.Server.Features.Notifications.Contracts;
using Atlas.Server.Features.Notifications.Queries;
using Atlas.Server.Features.PersonalApi;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;

namespace Atlas.Server.Features.Notifications;

/// <summary>
/// The token owner's inbox over MCP (inbox spec §8): the list of the web and /api/v1 and marking items read; the preferences stay the
/// web's. Only items whose object the owner may read now, and on MCP not from a space switched out of AI (G5). Not a Wolverine
/// handler: the class name does not end with "Handler".
/// </summary>
[McpServerToolType]
public sealed class NotificationMcpTools(IMessageBus bus)
{
    [McpServerTool(Name = "atlas_list_notifications", Title = "Oznámení", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true)]
    [McpToolScope(ApiScopes.Read)]
    [Description("Vypíše oznámení vlastníka tokenu (zmínky v komentářích a stránkách, sdílení, připomínky ověření stránek, kterým je garantem, " +
        "nahlášení neaktuální stránky a správcům prostoru deaktivovaného garanta), nejnovější událost první, 30 na stránku: notificationId, " +
        "druh, objectId a typ objektu, název, kdo naposledy, počet událostí, zda je přečtené a text komentáře nebo poznámky nahlášení (excerpt). Jen ta, jejichž objekt smíte číst. " +
        "Označí je atlas_mark_notifications_read. " + McpText.TrustNotice)]
    public async Task<CallToolResult> ListAsync(
        [Description("Volitelně next z předchozí stránky výpisu.")] string? before = null,
        CancellationToken ct = default)
    {
        var list = await bus.InvokeAsync<NotificationList>(new ListNotifications(McpArguments.OptionalId(before, "before")), ct);
        var body = new StringBuilder();
        var items = new JsonArray();
        foreach (var item in list.Items)
        {
            var at = item.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            body.Append(item.Read ? "- " : "- [nepřečtené] ").Append(item.Kind).Append(": ").Append(item.SubjectType is null ? "položka, ke které už nemáte přístup" : McpText.Line(item.SubjectTitle, 200))
                .Append(" (notificationId: ").Append(item.Id).Append(", objectId: ").Append(item.SubjectId).Append(", ").Append(item.SubjectType);
            if (item.ActorName is { } actor)
            {
                body.Append(", naposledy ").Append(McpText.Line(actor, 100));
            }
            body.Append(", ").Append(item.Count).Append("×, ").Append(item.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)).Append(')');
            // The comment's text or the „Nahlásit neaktuální“ note (NotificationReads moves it out of the anchor).
            if (item.Excerpt is { } excerpt)
            {
                body.Append(": ").Append(McpText.Quote(McpText.Line(excerpt, 500)));
            }
            body.Append('\n');
            items.Add(new JsonObject
            {
                ["notificationId"] = item.Id, ["kind"] = item.Kind, ["objectId"] = item.SubjectId, ["typeId"] = item.SubjectType, ["title"] = item.SubjectTitle,
                ["spaceId"] = item.SpaceId, ["anchor"] = item.Anchor, ["excerpt"] = item.Excerpt,["actorId"] = item.ActorId,
                ["actorName"] = item.ActorName, ["count"] = item.Count, ["updatedAt"] = at, ["read"] = item.Read
            });
        }
        return McpText.Result(McpText.Wrap($"Oznámení: {list.Items.Count} na stránce, nepřečtených {list.Unread}.",
                list.Items.Count == 0 ? "Žádná oznámení." : body.ToString(), list.Next is { } next ? $"Další oznámení: before {next}." : null),
            new JsonObject { ["items"] = items, ["unread"] = list.Unread, ["next"] = list.Next });
    }

    [McpServerTool(Name = "atlas_mark_notifications_read", Title = "Označit oznámení jako přečtená", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Označí oznámení jako přečtené; bez notificationId všechna.")]
    [Description("Označí oznámení vlastníka tokenu jako přečtené: jedno podle notificationId z atlas_list_notifications, nebo bez něj všechna. " +
        "Mění jen osobní stav, obsah ani ostatní čtenáře ne.")]
    public async Task<CallToolResult> MarkReadAsync([Description("Volitelně notificationId z atlas_list_notifications; bez něj všechna.")] string? notificationId = null,
        CancellationToken ct = default)
    {
        var marked = await bus.InvokeAsync<NotificationsMarked>(new MarkNotificationsRead(McpArguments.OptionalId(notificationId, "notificationId")), ct);
        return McpText.Result($"Označeno jako přečtené: {marked.Marked}. Nepřečtených zbývá {marked.Unread}.\n",
            new JsonObject { ["marked"] = marked.Marked, ["unread"] = marked.Unread });
    }
}
