using Wolverine;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Queries;

namespace Atlas.Server.Features.Mcp;

/// <summary>Administrace → Moduly a integrace → MCP server (W3): the switch with CAS, WikiAdmin only.</summary>
public static class McpEndpoints
{
    public static void MapMcpAdministration(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/mcp").RequireAuthorization();
        group.MapGet("", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<McpSettingsView>(new GetMcpSettings(), ct)))
            .Produces<McpSettingsView>()
            .ProducesProblem(403);
        group.MapPut("", async (SaveMcpRequest request, IMessageBus bus, McpModuleState state, CancellationToken ct) =>
            {
                var saved = await bus.InvokeAsync<McpSettingsView>(new SaveMcpSettings(request), ct);
                // After the commit: the next /mcp request reads the new state, not one cached before it.
                state.Invalidate();
                return Results.Ok(saved);
            })
            .Produces<McpSettingsView>()
            .ProducesProblem(403)
            .ProducesProblem(409);
    }
}
