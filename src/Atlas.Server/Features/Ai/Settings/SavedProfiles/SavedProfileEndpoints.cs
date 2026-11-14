using Atlas.Server.Features.Ai.Settings.Contracts;
using Wolverine;

namespace Atlas.Server.Features.Ai.Settings.SavedProfiles;

public static class SavedProfileEndpoints
{
    public static void MapSavedProfiles(this RouteGroupBuilder admin)
    {
        var saved = admin.MapGroup("/profiles/{kind}/saved");
        saved.MapGet("/", async (string kind, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<SavedProfileList>(new ListSavedProfiles(kind), ct)));
        saved.MapPost("/", async (string kind, SaveSavedProfileRequest request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<SavedProfileView>(new SaveSavedProfile(kind, null, request), ct)));
        saved.MapGet("/{id:guid}", async (string kind, Guid id, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<SavedProfileView>(new GetSavedProfile(kind, id), ct)));
        saved.MapPut("/{id:guid}", async (string kind, Guid id, SaveSavedProfileRequest request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<SavedProfileView>(new SaveSavedProfile(kind, id, request), ct)));
        saved.MapDelete("/{id:guid}", async (string kind, Guid id, string? expectedVersion, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync<bool>(new DeleteSavedProfile(kind, id, expectedVersion), ct);
            return Results.NoContent();
        });
        saved.MapPost("/from-current", async (string kind, SnapshotCurrentProfileRequest request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<SavedProfileView>(new SnapshotCurrentProfile(kind, request), ct)));
        saved.MapPost("/{id:guid}/activate", async (string kind, Guid id, ActivateSavedProfileRequest request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<AiProfileView>(new ActivateSavedProfile(kind, id, request), ct)));
        saved.MapPost("/models", async (string kind, SavedModelDiscoveryRequest request, SavedProfileConnectionRunner runner, CancellationToken ct)
            => Results.Ok(await runner.DiscoverAsync(kind, null, request, ct))).RequireRateLimiting("ai-admin-test");
        saved.MapPost("/{id:guid}/models", async (string kind, Guid id, SavedModelDiscoveryRequest request, SavedProfileConnectionRunner runner, CancellationToken ct)
            => Results.Ok(await runner.DiscoverAsync(kind, id, request, ct))).RequireRateLimiting("ai-admin-test");
        saved.MapPost("/{id:guid}/test", async (string kind, Guid id, SavedProfileVersionRequest request, SavedProfileConnectionRunner runner, CancellationToken ct)
            => Results.Ok(await runner.TestAsync(kind, id, request.ExpectedVersion, ct))).RequireRateLimiting("ai-admin-test");
    }
}
