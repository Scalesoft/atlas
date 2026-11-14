using System.Text.Json;
using ModelContextProtocol.Protocol;
using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.PersonalApi;

namespace Atlas.Server.Features.Mcp;

/// <summary>
/// The audit of every attempt of a tool with the write scope (D5), written by the call filter after the result, refusals
/// included (a missing scope, a version conflict, unsaved live edits, a refused conversion): the tool name as the operation, the
/// page and space it addressed, the outcome code (the error code, a successful result's structured "outcome" such as a declined publication,
/// else ok) and the resulting version. <see cref="ApiActivityAudit"/> writes it in its own short
/// transaction, so the write's rollback does not take it along, and never fails the call. A batch (atlas_apply_labels) is audited per item. Never content, a title or an error text:
/// only ids parsed as UUIDs, the error code and the version.
/// </summary>
public static class McpWriteAudit
{
    public static bool Audits(string? scope) => scope is ApiScopes.Write;

    public static Task RecordAsync(IServiceProvider services, string tool, CallToolRequestParams? call, CallToolResult? result, string? outcome = null)
    {
        var structured = result?.StructuredContent is { ValueKind: JsonValueKind.Object } value ? value : (JsonElement?)null;
        var failed = result?.IsError == true;
        outcome ??= failed ? Text(structured, "code") ?? ApiActivityOutcomes.Error : Text(structured, "outcome") ?? ApiActivityOutcomes.Ok;
        var audit = services.GetRequiredService<ApiActivityAudit>();
        if (ChangedPages(call) is { Count: > 0 } pages)
        {
            return RecordItemsAsync(audit, tool, pages, structured, failed, outcome);
        }
        var pageId = Id(structured, "pageId") ?? Argument(call, "pageId");
        var spaceId = Id(structured, "spaceId") ?? Argument(call, "spaceId");
        int? version = outcome == ApiActivityOutcomes.Ok && structured?.TryGetProperty("version", out var number) == true && number.TryGetInt32(out var parsed) ? parsed : null;
        return audit.RecordAsync(tool, pageId, spaceId, outcome, version);
    }

    /// <summary>
    /// A batch (atlas_apply_labels): one row per page of its changes, so the audit names every page the call addressed. A failed item
    /// carries its own code (the error's items), the other items of a failed batch rolled_back, as nothing of it was saved; a refusal
    /// before any item (scope, arguments) carries the call's outcome.
    /// </summary>
    private static async Task RecordItemsAsync(ApiActivityAudit audit, string tool, IReadOnlyList<Guid> pages, JsonElement? structured, bool failed, string outcome)
    {
        var codes = new Dictionary<int, string>();
        if (failed && structured?.TryGetProperty("items", out var items) == true && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("index", out var index) && index.TryGetInt32(out var number) && Text(item, "code") is { } code)
                {
                    codes[number] = code;
                }
            }
        }
        for (var index = 0; index < pages.Count; index++)
        {
            if (pages[index] == Guid.Empty)
            {
                continue;
            }
            var itemOutcome = codes.GetValueOrDefault(index) ?? (failed && codes.Count > 0 ? RolledBack : outcome);
            await audit.RecordAsync(tool, pages[index], null, itemOutcome, null);
        }
    }

    private const string RolledBack = "rolled_back";

    /// <summary>The pages of a batch's changes in their order, at most the tool's limit (a longer list is refused as a whole and audited once).</summary>
    private static IReadOnlyList<Guid>? ChangedPages(CallToolRequestParams? call)
    {
        if (call?.Arguments?.TryGetValue("changes", out var changes) != true || changes.ValueKind != JsonValueKind.Array
            || changes.GetArrayLength() > Tools.McpLabelTools.MaxChanges)
        {
            return null;
        }
        // An item without a valid pageId keeps its place (index), so the failed items' codes stay aligned; it is simply not recorded.
        var pages = changes.EnumerateArray().Select(change => change.ValueKind == JsonValueKind.Object && Text(change, "pageId") is { } text && Guid.TryParse(text, out var id) ? id : Guid.Empty).ToList();
        return pages.All(x => x == Guid.Empty) ? null : pages;
    }

    private static string? Text(JsonElement? element, string name)
        => element?.TryGetProperty(name, out var value) == true && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Guid? Id(JsonElement? element, string name)
        => Guid.TryParse(Text(element, name), out var id) ? id : null;

    private static Guid? Argument(CallToolRequestParams? call, string name)
        => call?.Arguments?.TryGetValue(name, out var value) == true && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id)
            ? id
            : null;
}
