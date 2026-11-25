using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Comments.Commands;
using Atlas.Server.Features.Comments.Contracts;
using Atlas.Server.Features.Comments.Queries;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PersonalApi;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;

namespace Atlas.Server.Features.Comments;

/// <summary>
/// Discussions over MCP (AI parity, 5.2: the page chat is the object's comments): the web's and /api/v1's read, post, edit and delete
/// as the token owner. Rights and G5 are decided in <see cref="CommentGate"/> and the handlers (the author edits; the author or the
/// object's manager deletes); their ApiExceptions become tool errors in the call filter, which also audits the writes. Not a Wolverine handler: the class name does not end with "Handler".
/// </summary>
[McpServerToolType]
public sealed class CommentMcpTools(IMessageBus bus)
{
    private const int Budget = McpText.Limit / 2 - 2_000;

    [McpServerTool(Name = "atlas_list_comments", Title = "Komentáře objektu", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Komentáře (diskuse) stránky, tabule nebo jiného objektu, nejnovější první, po 20. objectId, volitelně before.")]
    [Description("Vrátí komentáře (diskusi) objektu, který smíte číst: stránky (objectId = pageId), tabule (boardId), kontaktu…; nejnovější " +
        "první, po 20. U každého id, autor, čas, text v Markdownu a místo (anchor = id nadpisu či položky). Starší komentáře: before = id " +
        "nejstaršího, který máte. count je počet živých komentářů, canPost zda smíte psát (atlas_post_comment). " + McpText.TrustNotice)]
    public async Task<CallToolResult> ListAsync(
        [Description("objectId stránky, tabule nebo jiného objektu.")] string objectId,
        [Description("Id nejstaršího komentáře z předchozího výsledku pro starší stránku.")] string? before = null,
        CancellationToken ct = default)
    {
        var id = McpArguments.RequiredId(objectId, "objectId");
        var page = await bus.InvokeAsync<CommentPage>(new GetComments(id, McpArguments.OptionalId(before, "before")), ct);
        var body = new StringBuilder();
        var items = new JsonArray();
        CommentDto? last = null;
        foreach (var comment in page.Items)
        {
            var line = "- " + comment.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC, " + McpText.Line(comment.AuthorName, 100) +
                " (commentId " + comment.Id + (comment.Anchor is { } anchor ? ", anchor " + anchor : "") + "):\n" +
                (comment.Deleted ? "  (komentář byl odstraněn)" : "  " + McpText.Content(comment.Body).Replace("\n", "\n  ")) + "\n";
            // The text travels twice (text block and structured content): half the limit each.
            if (body.Length + line.Length > Budget)
            {
                break;
            }
            body.Append(line);
            last = comment;
            items.Add(new JsonObject
            {
                ["commentId"] = comment.Id, ["authorId"] = comment.AuthorId, ["authorName"] = comment.AuthorName, ["body"] = comment.Body,
                ["anchor"] = comment.Anchor, ["createdAt"] = comment.CreatedAt, ["editedAt"] = comment.EditedAt, ["deleted"] = comment.Deleted
            });
        }
        var more = last is not null && (page.HasMore || items.Count < page.Items.Count);
        var footer = (page.CanPost ? "Smíte odpovědět (atlas_post_comment)." : "Psát sem nesmíte (komentáře prostoru jsou vypnuté).") +
            (more ? $"\nStarší komentáře: zavolejte atlas_list_comments s before: \"{last!.Id}\"." : "");
        return McpText.Result(McpText.Wrap($"Komentáře objektu {id}: {page.Count} celkem, zobrazeno {items.Count}.", body.ToString(), footer),
            new JsonObject
            {
                ["objectId"] = id, ["count"] = page.Count, ["canPost"] = page.CanPost, ["items"] = items,
                ["nextBefore"] = more ? last!.Id : null
            });
    }

    [McpServerTool(Name = "atlas_post_comment", Title = "Napsat komentář", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Napíše komentář (odpověď v diskusi) ke stránce, tabuli nebo jinému objektu; zmínka = odkaz na kontakt. objectId, body, anchor.")]
    [Description("Napíše komentář k objektu, který smíte číst, v prostoru se zapnutými komentáři. Diskuse je plochá: odpověď je další komentář " +
        "téhož objektu. Text je Markdown, nejvýše 10 000 znaků; zmínka člověka je odkaz na jeho kontakt [Jana Nová](a-contact:?internalUser.id=<userId>) " +
        "a dotyčný dostane oznámení. anchor volitelně váže komentář k nadpisu či položce (jejich id). Komentář uvidí čtenáři hned. " +
        "Přes MCP vyžaduje rozsah Zápis.")]
    public async Task<CallToolResult> PostAsync(
        [Description("objectId stránky, tabule nebo jiného objektu.")] string objectId,
        [Description("Text komentáře v Markdownu.")] string body,
        [Description("Volitelně id nadpisu nebo položky, ke které se komentář váže.")] string? anchor = null,
        CancellationToken ct = default)
    {
        var id = McpArguments.RequiredId(objectId, "objectId");
        var comment = await bus.InvokeAsync<CommentDto>(new PostComment(id, body, anchor), ct);
        return McpText.Result($"Komentář napsán.\n- objectId: {id}\n- commentId: {comment.Id}",
            new JsonObject { ["objectId"] = id, ["commentId"] = comment.Id, ["createdAt"] = comment.CreatedAt });
    }

    [McpServerTool(Name = "atlas_edit_comment", Title = "Upravit komentář", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Přepíše text vlastního komentáře objektu (označí se „upraveno“). objectId, commentId, body.")]
    [Description("Přepíše text komentáře, jehož jste autorem, v prostoru se zapnutými komentáři; komentář se označí „upraveno“, starý text se " +
        "nezachová. Text je Markdown, nejvýše 10 000 znaků; nově zmínění lidé dostanou oznámení. Cizí komentář upravit nelze. " +
        "Přes MCP vyžaduje rozsah Zápis.")]
    public async Task<CallToolResult> EditAsync(
        [Description("objectId stránky, tabule nebo jiného objektu.")] string objectId,
        [Description("commentId komentáře (z atlas_list_comments).")] string commentId,
        [Description("Nový text komentáře v Markdownu.")] string body,
        CancellationToken ct = default)
    {
        var id = McpArguments.RequiredId(objectId, "objectId");
        var comment = await bus.InvokeAsync<CommentDto>(new EditComment(id, McpArguments.RequiredId(commentId, "commentId"), body), ct);
        return McpText.Result($"Komentář upraven.\n- objectId: {id}\n- commentId: {comment.Id}",
            new JsonObject { ["objectId"] = id, ["commentId"] = comment.Id, ["editedAt"] = comment.EditedAt });
    }

    [McpServerTool(Name = "atlas_delete_comment", Title = "Smazat komentář", Destructive = true, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Smaže komentář objektu (vlastní, nebo jako správce objektu jakýkoli). objectId, commentId.")]
    [Description("Smaže komentář: vlastní, nebo jako správce objektu (správce wiki, správce prostoru, vlastník soukromého objektu) jakýkoli, " +
        "i při vypnutých komentářích. V diskusi zůstane místo něj „komentář byl odstraněn“; text, zmínky a místo zmizí a vrátit to nejde. " +
        "Přes MCP vyžaduje rozsah Zápis.")]
    public async Task<CallToolResult> DeleteAsync(
        [Description("objectId stránky, tabule nebo jiného objektu.")] string objectId,
        [Description("commentId komentáře (z atlas_list_comments).")] string commentId,
        CancellationToken ct = default)
    {
        var id = McpArguments.RequiredId(objectId, "objectId");
        var comment = McpArguments.RequiredId(commentId, "commentId");
        await bus.InvokeAsync(new DeleteComment(id, comment), ct);
        return McpText.Result($"Komentář smazán.\n- objectId: {id}\n- commentId: {comment}",
            new JsonObject { ["objectId"] = id, ["commentId"] = comment });
    }
}
