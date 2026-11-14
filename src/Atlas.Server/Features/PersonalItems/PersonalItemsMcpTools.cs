using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolverine;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Objects.Queries;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.PersonalItems.Contracts;
using Atlas.Server.Features.PersonalItems.Queries;
using Atlas.Server.Platform.Resources;

namespace Atlas.Server.Features.PersonalItems;

/// <summary>
/// Personal items over MCP (AI parity, spec 2026-10-29 R5): Můj prostor and Sdíleno se mnou, and a note read, created, changed and
/// trashed through the one object path, as the token owner (its owner or a grantee; never an administrator's view). Sharing is a security change and has no tool (owner, 27. 10. 2026).
/// Errors are the handlers' ApiExceptions. Not a Wolverine handler.
/// </summary>
[McpServerToolType]
public sealed class PersonalItemsMcpTools(IMessageBus bus)
{
    private const string Private = "Osobní položky vidí jen vlastník a ti, se kterými je sdílí; správci wiki ne.";

    [McpServerTool(Name = "atlas_list_personal", Title = "Můj prostor", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Moje soukromé poznámky (Můj prostor), nejnovější změna první; koš je atlas_list_object_trash.")]
    [Description("Vypíše Můj prostor vlastníka tokenu: jeho soukromé poznámky (atlas.note), nejnovější změna první, s počtem sdílení a úryvkem textu. " +
        "Celou poznámku čte atlas_get_note. Koš je atlas_list_object_trash se scope private, obnova atlas_restore_object; sdílení zůstane. " +
        Private + " " + McpText.TrustNotice)]
    public async Task<CallToolResult> ListPersonalAsync(CancellationToken ct = default)
    {
        return List("Můj prostor", await bus.InvokeAsync<IReadOnlyList<PersonalItemDto>>(new ListPersonalItems(), ct));
    }

    [McpServerTool(Name = "atlas_list_shared_with_me", Title = "Sdíleno se mnou", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Poznámky, které mi jiní sdíleli (Sdíleno se mnou), s vlastníkem, úrovní a koncem platnosti.")]
    [Description("Vypíše Sdíleno se mnou: osobní poznámky jiných lidí, které vlastníkovi tokenu (nebo jeho skupině) právě teď sdílejí, s vlastníkem, " +
        "úrovní read/write a koncem platnosti. " + Private + " " + McpText.TrustNotice)]
    public async Task<CallToolResult> ListSharedAsync(CancellationToken ct = default)
    {
        return List("Sdíleno se mnou", await bus.InvokeAsync<IReadOnlyList<PersonalItemDto>>(new ListSharedWithMe(), ct));
    }

    [McpServerTool(Name = "atlas_get_note", Title = "Poznámka", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [McpToolScope(ApiScopes.Read)]
    [AssistantDescription("Název a text (Markdown) soukromé poznámky. noteId.")]
    [Description("Vrátí soukromou poznámku: název (jednořádkový Markdown), text (markdown), verzi a zda ji smíte měnit (atlas_edit_note). " + McpText.TrustNotice)]
    public async Task<CallToolResult> GetNoteAsync([Description("noteId z atlas_list_personal nebo atlas_list_shared_with_me.")] string noteId, CancellationToken ct = default)
    {
        var note = await NoteAsync(McpArguments.RequiredId(noteId, "noteId"), ct);
        var title = note.Data?["title"]?.GetValue<string>() ?? "";
        var text = note.Data?["text"]?.GetValue<string>() ?? "";
        return McpText.Result(McpText.Wrap($"Poznámka {McpText.Quote(McpText.Line(note.Title, 200))} ({note.Id}), verze {note.Version}.", $"# {title}\n\n{text}",
                note.CanEdit ? "Smíte měnit (atlas_edit_note)." : "Jen ke čtení."),
            new JsonObject { ["noteId"] = note.Id, ["title"] = title, ["markdown"] = text, ["version"] = note.Version, ["canEdit"] = note.CanEdit });
    }

    [McpServerTool(Name = "atlas_create_note", Title = "Založit poznámku", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Založí soukromou poznámku v Mém prostoru. title, text?.")]
    [Description("Založí soukromou poznámku v Mém prostoru vlastníka tokenu. Vidí ji jen on, dokud ji sám ve webu nesdílí. Název je jeden řádek Markdownu " +
        "(nejvýše 200 znaků), text plný Markdown. Do koše ji přesune atlas_trash_note.")]
    public async Task<CallToolResult> CreateNoteAsync(
        [Description("Název poznámky, jeden řádek.")] string title,
        [Description("Volitelně text poznámky v Markdownu.")] string? text = null,
        CancellationToken ct = default)
    {
        var created = await bus.InvokeAsync<ObjectDto>(new CreateObject(NoteObjectType.Id, ObjectScopes.Private, null,
            new JsonObject { ["title"] = title, ["text"] = text ?? "" }), ct);
        return McpText.Result($"Poznámka založena.\n- noteId: {created.Id}\n- název: {McpText.Line(created.Title, 200)}\n",
            new JsonObject { ["noteId"] = created.Id, ["title"] = created.Title, ["version"] = created.Version });
    }

    [McpServerTool(Name = "atlas_edit_note", Title = "Upravit poznámku", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Změní název nebo text soukromé poznámky. noteId, title?, text?, expectedVersion?.")]
    [Description("Změní název nebo text (celý, Markdown) poznámky, kterou smíte měnit: svou, nebo sdílenou s úrovní write. S expectedVersion se změna zapíše jen na " +
        "tu verzi (jinak chyba version_conflict). Komu je sdílená, ten změnu uvidí hned.")]
    public async Task<CallToolResult> EditNoteAsync(
        [Description("noteId poznámky.")] string noteId,
        [Description("Volitelně nový název, jeden řádek.")] string? title = null,
        [Description("Volitelně nový celý text v Markdownu.")] string? text = null,
        [Description("Volitelně verze, na kterou se změna smí zapsat (version z atlas_get_note).")] long? expectedVersion = null,
        CancellationToken ct = default)
    {
        var id = McpArguments.RequiredId(noteId, "noteId");
        await NoteAsync(id, ct);
        var fields = new JsonObject();
        if (title is not null) fields["title"] = title;
        if (text is not null) fields["text"] = text;
        if (fields.Count == 0)
        {
            throw new ApiException(400, "Zadejte nový název nebo text.");
        }
        var result = await bus.InvokeAsync<ObjectChangeResult>(new ApplyObjectChange(id, Guid.NewGuid(), expectedVersion, new JsonObject { ["fields"] = fields }), ct);
        return McpText.Result($"Poznámka upravena.\n- noteId: {id}\n- verze: {result.Version}\n", new JsonObject { ["noteId"] = id, ["version"] = result.Version });
    }

    [McpServerTool(Name = "atlas_trash_note", Title = "Poznámku do koše", Destructive = true, Idempotent = false, OpenWorld = false, ReadOnly = false)]
    [McpToolScope(ApiScopes.Write)]
    [AssistantDescription("Přesune vlastní soukromou poznámku do koše. noteId.")]
    [Description("Přesune vlastní poznámku do koše; zmizí z Mého prostoru i ze Sdíleno se mnou u všech, komu je sdílená. Sdílenou cizí poznámku do koše dát nelze.")]
    public async Task<CallToolResult> TrashNoteAsync([Description("noteId poznámky.")] string noteId, CancellationToken ct = default)
    {
        var id = McpArguments.RequiredId(noteId, "noteId");
        await NoteAsync(id, ct);
        await bus.InvokeAsync(new TrashObject(id), ct);
        return McpText.Result($"Poznámka je v koši.\n- noteId: {id}\n", new JsonObject { ["noteId"] = id });
    }

    /// <summary>The note, readable by the token owner; an object of another type is as missing as an unreadable one.</summary>
    private async Task<ObjectDto> NoteAsync(Guid id, CancellationToken ct)
    {
        var note = await bus.InvokeAsync<ObjectDto>(new GetObject(id), ct);
        return note.TypeId == NoteObjectType.Id ? note : throw new ApiException(404, "Poznámka nebyla nalezena.");
    }

    private static CallToolResult List(string heading, IReadOnlyList<PersonalItemDto> items)
    {
        var body = new StringBuilder();
        var rows = new JsonArray();
        foreach (var item in items)
        {
            body.Append("- ").Append(McpText.Line(item.Title, 200)).Append(" (").Append(item.TypeId).Append(", id ").Append(item.Id);
            if (item.OwnerName is { } owner)
            {
                body.Append(", sdílí ").Append(McpText.Line(owner, 100)).Append(", ").Append(item.Level);
                if (item.ExpiresAt is { } ends)
                {
                    body.Append(" do ").Append(ends.ToString("O"));
                }
            }
            else if (item.Shares > 0)
            {
                body.Append(", sdíleno: ").Append(item.Shares);
            }
            body.Append(")\n");
            rows.Add(new JsonObject
            {
                ["id"] = item.Id, ["typeId"] = item.TypeId, ["title"] = item.Title, ["updatedAt"] = item.UpdatedAt, ["canEdit"] = item.CanEdit,
                ["shares"] = item.Shares, ["ownerName"] = item.OwnerName, ["level"] = item.Level, ["expiresAt"] = item.ExpiresAt, ["excerpt"] = item.Excerpt
            });
        }
        return McpText.Result(McpText.Wrap($"{heading}: {items.Count} položek.", body.Length == 0 ? "(prázdné)" : body.ToString()), new JsonObject { ["items"] = rows });
    }
}
