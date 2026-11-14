using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Assistant.Commands;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Persistence;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Queries;
using Wolverine;

namespace Atlas.Server.Features.Assistant.Agent;

/// <summary>
/// How a write tool's call becomes the items of a proposal card and back (spec §6.1). The only place that knows it: a write tool
/// without a shape is not proposable and keeps the refusal <c>confirmation_required</c> until its work package adds a shape with a
/// server-built preview. <see cref="ItemsArgument"/> names the array whose elements are the items (a batch tool: N changes = N
/// selectable items, one call with the chosen ones); null makes the whole call one item (a single-change tool).
/// </summary>
public sealed record AiProposalShape(string Tool, string? ItemsArgument, string Kind)
{
    public const int MaxItems = McpPageMove.MaxMoves;
    public const string Labels = "labels";
    public const string Content = "content";
    public const string Rename = "rename";
    public const string NewDraft = "draft";
    public const string Move = "move";
    public const string Publish = "publish";

    public static AiProposalShape? For(string tool) => tool switch
    {
        // WP-21, spec §5.2: changes[] (1–100) of { pageId, icon?+color? or clearDecoration?, addTags?, removeTags?,
        // expectedRevisionId?, expectedTagIds? }; one transaction, all or nothing.
        "atlas_apply_labels" => new(tool, "changes", Labels),
        // WP-31, spec §7.2: one call = one item, previewed by AssistantDraftPreview. "Přeformátuj jako X" is one atlas_replace_page.
        "atlas_update_section" or "atlas_insert_section" or "atlas_replace_page" => new(tool, null, Content),
        "atlas_rename_page" => new(tool, null, Rename),
        "atlas_create_draft" => new(tool, null, NewDraft),
        // Plan 3.5 C: a new deployment as a copy of another, with modules added; a new draft page, previewed by AssistantDraftPreview.
        "projector_create_deployment" => new(tool, null, NewDraft),
        // WP-40, WP-41: one call = one page, previewed by AssistantPublishScopePreview. Trash and restore never get a shape (spec §5.1).
        "atlas_move_page" or "atlas_move_page_to_space" => new(tool, null, Move),
        // The left menu of a space: moves[] (1–100), one row per move, applied in order in one transaction, all or nothing.
        "atlas_arrange_pages" => new(tool, "moves", Move),
        "atlas_publish" => new(tool, null, Publish),
        _ => null,
    };

    /// <summary>The items of a call, in order; null when the items argument is not an array of objects.</summary>
    public IReadOnlyList<JsonObject>? Items(JsonObject arguments)
        => ItemsArgument is null ? [arguments]
            : arguments[ItemsArgument] is JsonArray array && array.All(item => item is JsonObject) ? array.Cast<JsonObject>().ToArray() : null;

    /// <summary>The call that runs the chosen items: the stored arguments with only those elements of the items argument, in order.</summary>
    public JsonObject Select(JsonObject arguments, IReadOnlyList<int> indexes)
    {
        var call = (JsonObject)arguments.DeepClone();
        if (ItemsArgument is not null)
        {
            var items = Items(arguments)!;
            call[ItemsArgument] = new JsonArray(indexes.Select(index => items[index].DeepClone()).ToArray());
        }
        return call;
    }
}

/// <summary>A proposal built during a turn, stored with the released answer (<see cref="ReleaseAgentAnswer"/>). Prints no content.</summary>
public sealed record AiProposalDraft(string Tool, string Arguments, IReadOnlyList<AiProposalItemView> Items)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Tool = ").Append(Tool).Append(", Items = ").Append(Items.Count);
        return true;
    }
}

/// <summary>
/// Proposals of agent writes (spec §6, D4, WP-20): the write seam of <see cref="AssistantToolSession"/> turns an offered write call
/// into a proposal instead of running it; the owner confirms the chosen items and only then the stored call runs, in a fresh
/// <see cref="AssistantToolSession"/> on the assistant channel, by the same MCP tool and shared handlers.
/// <para>
/// Interception, not <c>ApprovalRequiredAIFunction</c>: the seam answers the model with a tool result ("the proposal waits for the
/// user"), so the loop ends normally with the model's own text, and a proposal the server cannot preview (an unknown icon, a page the
/// user may not read, over 100 changes) is a tool error the model reads and fixes within the same turn. The approval types of
/// Microsoft.Extensions.AI would end the loop at the call and give the model no such chance; nothing here depends on them.
/// </para>
/// </summary>
public sealed class AssistantProposals(IMessageBus bus, IHttpContextAccessor http, ApiActivityAudit audit, ILogger<AssistantProposals> logger)
{
    public const string ProposalOperation = "assistant.proposal";
    public const string DecisionOperation = "assistant.decision";

    public const string ProposedText =
        "Návrh je uložený a čeká na potvrzení uživatelem na kartě v panelu; zatím se nic nezměnilo. Napiš stručně, co navrhuješ, a požádej " +
        "uživatele, ať návrh zkontroluje, případně odškrtne položky, a potvrdí ho. Další zápis v tomto tahu nevolej.";

    /// <summary>The answer when the model left none after proposing: the card must not be lost with an empty answer.</summary>
    public const string FallbackAnswer = "Návrh změn je připravený na kartě níže. Zkontrolujte ho a potvrďte.";

    public Task<AiProposalView> GetAsync(Guid id, CancellationToken ct) => bus.InvokeAsync<AiProposalView>(new GetAiProposal(id), ct);

    /// <summary>
    /// <c>POST /api/ai/proposals/{id}/decision</c>: the owner's pending proposal (404 otherwise), the "AI s nástroji" right again (D17,
    /// D19: the content rights are the shared handlers'), 409 <c>proposal_expired</c> / <c>proposal_superseded</c> / <c>proposal_decided</c>.
    /// Declining writes nothing else. Approving runs the stored call with only the chosen items once, to the end even when the client
    /// goes away, and stores the result per item (spec S4: no model call).
    /// </summary>
    public async Task<AiProposalView> DecideAsync(Guid id, DecideAiProposalRequest request, CancellationToken ct)
    {
        if (request.Approve is not { } approve)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "Zvolte potvrzení, nebo zrušení návrhu.");
        }
        var context = http.HttpContext ?? throw new InvalidOperationException("A decision runs in a request.");
        // The decision is the assistant's: its audit, and every write of the call, carry the channel assistant without a token.
        context.Items[HttpRequestChannel.AssistantMarker] = true;
        var claim = await bus.InvokeAsync<AiProposalClaim>(new ClaimAiProposal(id, approve, request.Items ?? []), ct);
        if (claim.Declined is { } declined)
        {
            await audit.RecordAsync(DecisionOperation, null, null, declined.State, null);
            return declined;
        }
        var row = claim.Row!;
        context.Items[HttpRequestChannel.AssistantConversation] = row.ConversationId;
        CallToolResult result;
        try
        {
            var shape = AiProposalShape.For(row.Tool) ?? throw new InvalidOperationException("The tool has no proposal shape.");
            result = await AssistantToolSession.ExecuteAsync(context, row.Tool, shape.Select(JsonNode.Parse(row.Arguments)!.AsObject(), claim.Selected),
                CancellationToken.None);
        }
        catch (Exception error)
        {
            // Never the exception text: it can quote the arguments.
            logger.LogError("Assistant proposal {ProposalId} failed ({ErrorType})", id, error.GetType().Name);
            result = McpErrors.Error(500, "internal_error", "Nástroj se nepodařilo dokončit.");
        }
        var view = await bus.InvokeAsync<AiProposalView>(new FinishAiProposal(id, result.IsError != true, Results(row.Items.Count, claim.Selected, result)),
            CancellationToken.None);
        await audit.RecordAsync(DecisionOperation, SinglePage(view), null, view.State, null);
        return view;
    }

    /// <summary>Audits a proposal made in a turn (spec §6.2: who, when, tool; never content).</summary>
    public Task AuditProposalAsync(AiProposalView proposal) => audit.RecordAsync(ProposalOperation, SinglePage(proposal), null, proposal.State, null);

    private static Guid? SinglePage(AiProposalView view)
        => view.Items.Select(item => item.PageId).Where(id => id != Guid.Empty).Distinct().ToArray() is [var page] ? page : null;

    /// <summary>
    /// The result per item of the original proposal. The call ran with only the chosen items, re-indexed from 0 in their order, so a
    /// failing item the tool names (<c>items[].index</c>, WP-21: all or nothing, only failing ones listed) maps back to
    /// <paramref name="selected"/>[index]. Success: the chosen items applied. Failure: each failing item with its own code, the other
    /// chosen ones <c>rolled_back</c> (nothing was saved; the word of WP-21's audit), or every chosen one with the call's code when it
    /// names no item.
    /// Unchosen items are skipped. A one-item call that succeeded keeps the page it wrote (<c>pageId</c>): a new draft's page.
    /// </summary>
    public static IReadOnlyList<AiProposalResult> Results(int count, IReadOnlyList<int> selected, CallToolResult result)
    {
        var chosen = selected.Select((original, position) => (original, position)).ToDictionary(x => x.original, x => x.position);
        var structured = result.StructuredContent is { ValueKind: JsonValueKind.Object } value ? value : (JsonElement?)null;
        var failing = new Dictionary<int, string>();
        if (result.IsError == true && structured?.TryGetProperty("items", out var items) == true && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("index", out var index) && index.TryGetInt32(out var at)
                    && at >= 0 && at < selected.Count)
                {
                    failing[at] = Code(item) ?? "error";
                }
            }
        }
        var code = Code(structured) ?? "error";
        var written = count == 1 && result.IsError != true && structured?.TryGetProperty("pageId", out var pageId) == true
            && pageId.ValueKind == JsonValueKind.String && Guid.TryParse(pageId.GetString(), out var id) ? id : (Guid?)null;
        return Enumerable.Range(0, count).Select(index => !chosen.TryGetValue(index, out var position) ? new AiProposalResult(index, "skipped")
            : result.IsError != true ? new AiProposalResult(index, "applied", PageId: written)
            : new AiProposalResult(index, "failed", failing.Count == 0 ? code : failing.GetValueOrDefault(position) ?? "rolled_back")).ToArray();

        static string? Code(JsonElement? element)
            => element?.TryGetProperty("code", out var code) == true && code.ValueKind == JsonValueKind.String && code.GetString() is { Length: > 0 } text
                ? text
                : null;
    }

    /// <summary>
    /// Builds the proposal of one write call (spec §6.1 step 2). Labels: every item names a page the user may read now (the shared
    /// read of <c>atlas_get_labels</c>: content rights and G5), its title and path, its current labels and the proposed ones. A batch of
    /// moves: <see cref="AssistantPublishScopePreview.ArrangeAsync"/>. Values
    /// the server would refuse (an icon or colour outside <see cref="AppearanceCatalog"/>) refuse the whole proposal, so a card never
    /// shows an impossible change. A draft write: <see cref="AssistantDraftPreview"/>; a move or a publication:
    /// <see cref="AssistantPublishScopePreview"/>. A refusal is a tool error for the model.
    /// </summary>
    public static async Task<(AiProposalDraft? Draft, CallToolResult? Error)> DraftAsync(IServiceProvider services, AiProposalShape shape,
        JsonObject arguments, CancellationToken ct)
    {
        if (shape.Kind != AiProposalShape.Labels)
        {
            try
            {
                if (shape.Tool == "atlas_arrange_pages")
                {
                    return (new AiProposalDraft(shape.Tool, arguments.ToJsonString(), await AssistantPublishScopePreview.ArrangeAsync(services, shape, arguments, ct)), null);
                }
                var item = shape.Kind is AiProposalShape.Move or AiProposalShape.Publish
                    ? await AssistantPublishScopePreview.ItemAsync(services, shape, arguments, ct)
                    : await AssistantDraftPreview.ItemAsync(services, shape, arguments, ct);
                return (new AiProposalDraft(shape.Tool, arguments.ToJsonString(), [item]), null);
            }
            catch (ApiException error)
            {
                return (null, McpErrors.From(error));
            }
        }
        var bus = services.GetRequiredService<IMessageBus>();
        var changes = shape.Items(arguments);
        if (changes is null || changes.Count == 0)
        {
            return Refuse($"Zadej {shape.ItemsArgument} jako neprázdný seznam změn.");
        }
        if (changes.Count > AiProposalShape.MaxItems)
        {
            return Refuse($"Jeden návrh má nejvýše {AiProposalShape.MaxItems} změn; navrhni méně stránek.");
        }
        var labels = new Dictionary<Guid, McpLabels>();
        var pageIds = new Guid[changes.Count];
        for (var i = 0; i < changes.Count; i++)
        {
            if (!Guid.TryParse(Text(changes[i]["pageId"]), out pageIds[i]))
            {
                return Refuse($"Změna {i + 1}: chybí platné pageId.");
            }
            if (labels.ContainsKey(pageIds[i]))
            {
                // WP-21 refuses the same page twice in one call; the card would show two contradicting rows.
                return Refuse($"Změna {i + 1}: stránka už je v návrhu; dej všechny její změny do jedné položky.");
            }
            try
            {
                labels[pageIds[i]] = await bus.InvokeAsync<McpLabels>(new GetMcpLabels(pageIds[i]), ct);
            }
            catch (ApiException error)
            {
                return (null, McpErrors.Error(error.Status, error.Code ?? McpErrors.CodeFor(error.Status), $"Změna {i + 1}: {error.Message}"));
            }
        }
        var pages = (await bus.InvokeAsync<AiProposalPages>(new PreviewAiProposalPages(
            labels.Values.Select(page => new AiProposalPageRequest(page.PageId, page.SpaceId)).ToArray()), ct)).Items;
        var items = new AiProposalItemView[changes.Count];
        for (var i = 0; i < changes.Count; i++)
        {
            var current = labels[pageIds[i]];
            var before = new AiProposalLabels(current.Decoration?.Icon, current.Decoration?.Color, current.Tags.Select(tag => tag.Name).ToArray());
            if (After(changes[i], before) is not { } after)
            {
                return Refuse($"Změna {i + 1}: neplatná nebo prázdná změna. Ikonu a barvu ber jen z atlas_list_appearance a zadávej je spolu " +
                    "(nebo clearDecoration), tagy jako seznamy názvů addTags a removeTags.");
            }
            var page = pages.GetValueOrDefault(pageIds[i]);
            items[i] = new AiProposalItemView(i, pageIds[i], page?.Title ?? "", page?.Path ?? "", shape.Kind, before, after);
        }
        return (new AiProposalDraft(shape.Tool, arguments.ToJsonString(), items), null);

        static (AiProposalDraft?, CallToolResult?) Refuse(string message) => (null, McpErrors.Error(400, "invalid_proposal", message));
    }

    /// <summary>The labels after a change (spec §5.2, WP-21): icon and colour together or <c>clearDecoration</c>, omitted = unchanged;
    /// tags = current + <c>addTags</c> − <c>removeTags</c> (case-insensitive, as the server). Null for a change the server refuses or
    /// one that changes nothing.</summary>
    private static AiProposalLabels? After(JsonObject change, AiProposalLabels before)
    {
        var clear = change["clearDecoration"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
        var icon = Text(change["icon"]);
        var color = Text(change["color"]);
        var add = Names(change["addTags"]);
        var remove = Names(change["removeTags"]);
        if (clear && (icon ?? color) is not null
            || (icon is null) != (color is null)
            || icon is not null && (!AppearanceCatalog.Icons.Contains(icon) || !AppearanceCatalog.Colors.Contains(color!))
            || add is null && change["addTags"] is not null || remove is null && change["removeTags"] is not null
            || !clear && icon is null && (add ?? []).Count == 0 && (remove ?? []).Count == 0)
        {
            return null;
        }
        var tags = before.Tags.Where(tag => !(remove ?? []).Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var tag in add ?? [])
        {
            if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(tag);
            }
        }
        return clear ? new AiProposalLabels(null, null, tags) : new AiProposalLabels(icon ?? before.Icon, color ?? before.Color, tags);
    }

    internal static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static List<string>? Names(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }
        var names = array.Select(item => Text(item)?.Trim() ?? "").ToList();
        return names.Any(name => name.Length == 0) ? null : names;
    }

    /// <summary>
    /// What became of a proposal, for the next agent turn's history (the model never sees its earlier tool calls): the decision and,
    /// once executed, each item with its page, result and proposed labels, so "a teď stejně pro Personální" can repeat it.
    /// </summary>
    public static string Outcome(AiToolProposalRow row, DateTime now)
    {
        var text = new StringBuilder("[Výsledek návrhu změn: ");
        switch (row.EffectiveState(now))
        {
            case AiProposalState.Declined:
                return text.Append("uživatel návrh zrušil, nic se nezměnilo.]").ToString();
            case AiProposalState.Expired or AiProposalState.Superseded or AiProposalState.Pending:
                return text.Append("uživatel návrh nepotvrdil, nic se nezměnilo.]").ToString();
            case AiProposalState.Applying:
                return text.Append("provádí se.]").ToString();
        }
        var view = row.View(now);
        text.Append(view.State == AiProposalState.Applied ? "provedeno." : "selhalo, nic se nezměnilo.");
        foreach (var item in view.Items)
        {
            text.Append("\n- ").Append(item.Title).Append(" (pageId ").Append(item.PageId).Append("): ").Append(item.Status switch
            {
                "applied" => "provedeno",
                "skipped" => "uživatel nevybral, beze změny",
                _ => "selhalo (" + item.Code + ")",
            });
            switch (item.Kind)
            {
                case AiProposalShape.Labels:
                    text.Append("; navrženo: ikona ").Append(item.After?.Icon ?? "žádná").Append(", barva ").Append(item.After?.Color ?? "žádná")
                        .Append(", tagy ").Append(item.After is { Tags.Count: > 0 } after ? string.Join(", ", after.Tags) : "žádné");
                    break;
                case AiProposalShape.Rename:
                    text.Append("; navržený název: ").Append(item.Text?.After);
                    break;
                case AiProposalShape.NewDraft:
                    text.Append("; nový koncept v ").Append(item.Path);
                    break;
                case AiProposalShape.Move:
                    text.Append("; přesun z ").Append(item.Move?.Before).Append(" do ").Append(item.Move?.After);
                    break;
                case AiProposalShape.Publish:
                    text.Append("; publikace verze ").Append(item.Publish?.Version);
                    break;
                default:
                    text.Append("; koncept, sekce: ").Append(string.Join(", ", item.Text?.Sections ?? []));
                    break;
            }
        }
        return text.Append(']').ToString();
    }
}
