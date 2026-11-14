using Atlas.Server.Features.SpaceSettings.Commands;
using Atlas.Server.Features.SpaceSettings.Contracts;
using Atlas.Server.Features.SpaceSettings.Queries;
using Wolverine;

namespace Atlas.Server.Features.SpaceSettings;

public static class SpaceSettingsEndpoints
{
    public static void MapSpaceSettings(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/spaces", async (CreateSpaceRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<CreatedSpaceDto>(new CreateSpace(request), ct)))
            .RequireAuthorization()
            .Produces<CreatedSpaceDto>()
            .ProducesProblem(403);

        var settings = app.MapGroup("/api/spaces/{id:guid}/settings").RequireAuthorization();
        settings.MapGet("", async (Guid id, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<SpaceSettingsDto>(new GetSpaceSettings(id), ct)))
            .Produces<SpaceSettingsDto>()
            .ProducesProblem(404);
        settings.MapPut("", async (Guid id, SaveSpaceSettingsRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<SpaceSettingsDto>(new SaveSpaceSettings(id, request), ct)))
            .Produces<SpaceSettingsDto>()
            .ProducesProblem(409);
    }
}
