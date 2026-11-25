using ModelContextProtocol.Server;
using Atlas.Server.Platform.Modules;
using Atlas.Server.Features.Ai.Access;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PersonalApi;

namespace Atlas.Server.Features.Assistant.Agent;

/// <summary>One tool the assistant may use; <paramref name="Write"/>: it only proposes a change, which the user confirms.</summary>
public sealed record AssistantToolView(string Name, string Description, bool Write);

/// <summary>A group of tools (<see cref="AssistantToolGroups"/>) and the tools of it that exist for this user.</summary>
public sealed record AssistantToolGroupView(string Group, IReadOnlyList<AssistantToolView> Tools);

/// <summary>
/// <c>GET /api/assistant/tools</c>: what „S nástroji“ means for the signed-in user, from the very registry the agent turn offers
/// (<see cref="AssistantToolGroups"/> ∩ registered MCP tools ∩ not <see cref="AssistantToolSession.NeverOffered"/> ∩ a known scope; the
/// arrange group only while <c>assistant.arrange-pages</c> is on, the requirement group with the module). 404 when the agent is not
/// available to the user, so the UI shows nothing. Content rights and spaces excluded from AI are enforced per call, not here.
/// </summary>
public static class AssistantToolCatalog
{
    public const string Route = "/api/assistant/tools";

    public static async Task<IResult> HandleAsync(HttpContext context, AiCapabilities capabilities, AiUserAccess access, ModuleCatalog modules,
        IEnumerable<McpServerTool> registry, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var policy = await access.PolicyAsync(ct);
        if (!await capabilities.IsAvailableAsync(AiTaskCatalog.AssistantAgent, ct) || policy is not { Enabled: true, ToolsEnabled: true })
        {
            return Results.NotFound();
        }
        var arrange = await capabilities.IsAvailableAsync(AiTaskCatalog.AssistantArrange, ct);
        var projector = modules.IsEnabled("projector");
        var tools = registry.Where(tool => !AssistantToolSession.NeverOffered.Contains(tool.ProtocolTool.Name) && AssistantToolSession.ScopeOf(tool) is not null)
            .ToDictionary(tool => tool.ProtocolTool.Name, View, StringComparer.Ordinal);
        // The one tool that is no MCP tool (it reads through the session).
        tools[SummarizePagesTool.Name] = new(SummarizePagesTool.Name, "Shrne vybrané stránky.", false);
        return Results.Ok(AssistantToolGroups.Catalog
            .Where(group => (projector || !group.Projector) && (arrange || group.Name != AssistantToolGroups.MenuGroup))
            .Select(group => new AssistantToolGroupView(group.Name, [.. group.Tools.Where(tools.ContainsKey).Select(name => tools[name])]))
            .Where(group => group.Tools.Count > 0)
            .ToList());
    }

    private static AssistantToolView View(McpServerTool tool)
        => new(tool.ProtocolTool.Name,
            tool.Metadata.OfType<AssistantDescriptionAttribute>().FirstOrDefault()?.Text ?? tool.ProtocolTool.Description ?? "",
            AssistantToolSession.ScopeOf(tool) != ApiScopes.Read);
}
