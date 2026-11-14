using Atlas.Server.Features.PageTags.Domain;
using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Features.PageTags;

namespace Atlas.Server.Features.Mcp.Commands;

// The decoration and tag writes of MCP. Each runs the shared handler of the web and REST write inside its own transaction (the page
// lock, EditContent, validation, normalization, limits and the compare-and-set are the handler's) behind G5 decided under the space lock (taken FOR UPDATE up front, as the shared handler does). MCP adds a 404 for a page
// the actor may read but not edit (as the draft tools answer) and names the current state in a conflict, so the model can retry.
// Neither writes a document revision nor publishes: readers see the change at once.

public sealed class SetMcpDecorationHandler
{
    private readonly McpOrganizationGuard m_guard;
    private readonly PageDecorationHandler m_handler;
    private readonly PageDecorationReader m_reader;

    public SetMcpDecorationHandler(McpOrganizationGuard guard, PageDecorationHandler handler, PageDecorationReader reader)
    {
        m_guard = guard;
        m_handler = handler;
        m_reader = reader;
    }

    public async Task<PageDecorationState> Handle(SetMcpDecoration command, CancellationToken ct)
    {
        await m_guard.LockPageSpaceAsync(command.PageId, ct);
        try
        {
            return await m_handler.Handle(new SetPageDecoration(command.PageId, command.Request), ct);
        }
        catch (ApiException error) when (error.Code == "decoration_changed")
        {
            var current = await m_reader.ReadAsync(command.PageId, ct);
            throw new ApiException(409, "Ikona stránky byla mezitím změněna. Načtěte ji znovu (atlas_get_labels) a zopakujte zápis s novým expectedRevisionId.",
                "decoration_changed", new Dictionary<string, object?> { ["currentRevisionId"] = current?.RevisionId });
        }
    }
}

public sealed class SetMcpTagsHandler
{
    private readonly McpOrganizationGuard m_guard;
    private readonly PageTagsHandler m_handler;
    private readonly PageTagStore m_store;

    public SetMcpTagsHandler(McpOrganizationGuard guard, PageTagsHandler handler, PageTagStore store)
    {
        m_guard = guard;
        m_handler = handler;
        m_store = store;
    }

    public async Task<PageTagState> Handle(SetMcpTags command, CancellationToken ct)
    {
        await m_guard.LockPageSpaceAsync(command.PageId, ct);
        try
        {
            return await m_handler.Handle(new SetPageTags(command.PageId, command.Request), ct);
        }
        catch (ApiException error) when (error.Code == "tags_changed")
        {
            throw await ConflictAsync(m_store, command.PageId, ct);
        }
    }

    internal static async Task<ApiException> ConflictAsync(PageTagStore store, Guid pageId, CancellationToken ct)
        => new(409, "Štítky stránky byly mezitím změněny. Načtěte je znovu (atlas_get_labels) a zopakujte zápis s novými expectedTagIds.",
            "tags_changed", new Dictionary<string, object?> { ["currentTagIds"] = (await store.ReadAsync(pageId, ct)).Tags.Select(x => x.Id).ToArray() });
}

public sealed class ChangeMcpTagsHandler
{
    private readonly McpOrganizationGuard m_guard;
    private readonly PageTagsHandler m_handler;
    private readonly PageTagStore m_store;

    public ChangeMcpTagsHandler(McpOrganizationGuard guard, PageTagsHandler handler, PageTagStore store)
    {
        m_guard = guard;
        m_handler = handler;
        m_store = store;
    }

    public async Task<PageTagState> Handle(ChangeMcpTags command, CancellationToken ct)
    {
        await m_guard.LockPageSpaceAsync(command.PageId, ct);
        try
        {
            return await m_handler.Handle(new ChangePageTags(command.PageId, command.Request), ct);
        }
        catch (ApiException error) when (error.Code == "tags_changed")
        {
            throw await SetMcpTagsHandler.ConflictAsync(m_store, command.PageId, ct);
        }
    }
}

/// <summary>
/// atlas_apply_labels: every change through <see cref="SetMcpDecorationHandler"/> and <see cref="ChangeMcpTagsHandler"/> (so the shared
/// handlers, G5 and the MCP wording of a conflict as for one page) inside this message's one transaction. Lock order, whatever order the
/// changes come in: content shared, the pages' spaces ascending, the account, the new tag names in the catalog's order
/// (<see cref="TagNames.Prepare"/>), then the page rows ascending by id. A refused item does not stop the others, so the one error names
/// every failing item; the error rolls back what the others wrote.
/// </summary>
public sealed class ApplyMcpLabelsHandler
{
    private readonly McpOrganizationGuard m_guard;
    private readonly IContentAccess m_access;
    private readonly PageTagStore m_store;
    private readonly SetMcpDecorationHandler m_decoration;
    private readonly ChangeMcpTagsHandler m_tags;

    public ApplyMcpLabelsHandler(McpOrganizationGuard guard, IContentAccess access, PageTagStore store, SetMcpDecorationHandler decoration,
        ChangeMcpTagsHandler tags)
    {
        m_guard = guard;
        m_access = access;
        m_store = store;
        m_decoration = decoration;
        m_tags = tags;
    }

    public async Task<IReadOnlyList<McpAppliedLabels>> Handle(ApplyMcpLabels command, CancellationToken ct)
    {
        var changes = command.Changes;
        await m_guard.LockSpacesOfPagesAsync(changes.Select(x => x.PageId).ToArray(), ct);
        await ResolveNewTagsAsync(changes, ct);
        var applied = new McpAppliedLabels[changes.Count];
        var failed = new List<(int Index, ApiException Error)>();
        foreach (var index in Enumerable.Range(0, changes.Count).OrderBy(i => changes[i].PageId))
        {
            var change = changes[index];
            try
            {
                var decoration = change.Decoration is { } request ? await m_decoration.Handle(new SetMcpDecoration(change.PageId, request), ct) : null;
                var tags = change.Tags is { } tagRequest ? await m_tags.Handle(new ChangeMcpTags(change.PageId, tagRequest), ct) : null;
                applied[index] = new McpAppliedLabels(change.PageId, decoration, tags);
            }
            catch (ApiException error) when (error.Status is >= 400 and < 500)
            {
                failed.Add((index, error));
            }
        }
        return failed.Count == 0 ? applied : throw Failed(failed, changes);
    }

    /// <summary>Two batches adding the same new names to different pages would otherwise insert them in page order and wait on each
    /// other's catalog rows; each page's handler then finds its names already there. An invalid name is left to its item's error.</summary>
    private async Task ResolveNewTagsAsync(IReadOnlyList<McpLabelChange> changes, CancellationToken ct)
    {
        var names = new List<string>();
        foreach (var add in changes.Select(x => x.Tags?.Add).OfType<string[]>())
        {
            try
            {
                names.AddRange(TagNames.Prepare(add).Select(x => x.Name));
            }
            catch (ApiException)
            {
            }
        }
        if (names.Count == 0)
        {
            return;
        }
        var actor = await m_access.LockActorAsync(null, ct);
        foreach (var (name, normalized) in TagNames.Prepare(names))
        {
            await m_store.ResolveAsync(name, normalized, actor, ct);
        }
    }

    private static ApiException Failed(List<(int Index, ApiException Error)> failed, IReadOnlyList<McpLabelChange> changes)
    {
        var items = failed.OrderBy(x => x.Index).Select(x =>
        {
            var item = new Dictionary<string, object?>
            {
                ["index"] = x.Index, ["pageId"] = changes[x.Index].PageId, ["status"] = x.Error.Status,
                ["code"] = x.Error.Code ?? McpErrors.CodeFor(x.Error.Status), ["message"] = x.Error.Message
            };
            foreach (var (key, value) in x.Error.Extensions ?? new Dictionary<string, object?>())
            {
                item.TryAdd(key, value);
            }
            return item;
        }).ToArray();
        var first = items[0];
        var message = $"Nic se neuložilo: {items.Length} z {changes.Count} změn nelze provést. "
            + string.Join(" ", items.Select(x => $"changes[{x["index"]}] (pageId {x["pageId"]}): {x["code"]}, {x["message"]}"));
        return new ApiException((int)first["status"]!, message, (string)first["code"]!, new Dictionary<string, object?> { ["items"] = items });
    }
}
