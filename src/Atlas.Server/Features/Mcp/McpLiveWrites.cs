using System.Collections.Frozen;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Atlas.Server.Features.Collaboration.Domain;

namespace Atlas.Server.Features.Mcp;

/// <summary>
/// The MCP tools that write a page's content (3.6-H), for /mcp and the assistant's confirmed proposals alike: each runs as a live write of
/// its page (<see cref="LivePageWrites"/>), so a page open in live editing gets the change as a collaborator's update, with the agent among
/// its collaborators, and the call's <c>expectedVersion</c> is the version the agent read. Every other tool runs as it is.
/// </summary>
public static class McpLiveWrites
{
    /// <summary>What every such tool's description says about it.</summary>
    public const string Notice = "Má-li stránku někdo otevřenou, změna mu do editoru přijde živě jako od dalšího spolupracovníka a vy se mu mezitím " +
        "ukážete mezi spolupracovníky; nic, co píše, se nepřepíše. expectedVersion je verze, kterou jste četli: zápis odmítne (target_changed) " +
        "jen to, co mezitím někdo změnil právě v místě, které měníte.";

    public static readonly FrozenSet<string> Tools = FrozenSet.ToFrozenSet([
        "atlas_update_section", "atlas_insert_section", "atlas_replace_page", "atlas_update_embed", "atlas_link_page", "atlas_resolve_reference",
        "projector_update_requirement", "projector_update_requirements", "projector_create_requirement", "projector_insert_requirements",
        "projector_insert_from_deployment", "projector_move_requirement", "projector_remove_requirement", "atlas_undo_change"], StringComparer.Ordinal);

    public static async Task<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> request, Func<Task<CallToolResult>> call, CancellationToken ct)
    {
        var name = request.Params?.Name ?? "";
        var arguments = request.Params?.Arguments;
        if (!Tools.Contains(name) || arguments is null || !arguments.TryGetValue("pageId", out var page) || page.ValueKind != JsonValueKind.String
            || !Guid.TryParse(page.GetString(), out var pageId))
        {
            return await call();
        }
        int? version = null;
        if (arguments.TryGetValue("expectedVersion", out var expected))
        {
            // A value that is no whole number (a string, null, {}, [], 1.5) is the caller's input; the SDK would bind null as 0 (a version conflict).
            if (expected.ValueKind != JsonValueKind.Number || !expected.TryGetInt32(out var number))
            {
                throw new ApiException(400, "expectedVersion musí být celé číslo (verze z posledního čtení).", "invalid_argument");
            }
            version = number;
        }
        return await request.Services!.GetRequiredService<LivePageWrites>().RunAsync(pageId, version, name == "atlas_replace_page", call, ct, mcp: true);
    }
}
