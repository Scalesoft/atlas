using Atlas.Server.Features.SpaceAi.Commands;
using Atlas.Server.Features.SpaceAi.Contracts;
using Atlas.Server.Features.SpaceAi.Queries;
using Wolverine;

namespace Atlas.Server.Features.SpaceAi;

/// <summary>G5 "Zahrnout prostor do AI": space admins and WikiAdmins only; a member of the space gets 403, anyone else the 404 of a space it cannot see.</summary>
public static class SpaceAiEndpoints
{
    public static void MapSpaceAi(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/spaces/{spaceId:guid}/ai-settings").RequireAuthorization();
        settings.MapGet("", async (Guid spaceId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<SpaceAiSettingsView>(new GetSpaceAiSettings(spaceId), ct)))
            .Produces<SpaceAiSettingsView>()
            .ProducesProblem(404);
        settings.MapPut("", async (Guid spaceId, SaveSpaceAiSettingsRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<SpaceAiSettingsView>(new SaveSpaceAiSettings(spaceId, request), ct)))
            .Produces<SpaceAiSettingsView>()
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409);
    }
}
