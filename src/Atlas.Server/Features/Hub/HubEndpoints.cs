using Atlas.Server.Platform.Localization;
using Atlas.Server.Features.Hub.Commands;
using Atlas.Server.Features.Hub.Contracts;
using Atlas.Server.Features.Hub.Queries;
using Atlas.Server.Features.Hub.Migration;
using Wolverine;

namespace Atlas.Server.Features.Hub;

public static class HubEndpoints
{
    public static IEndpointRouteBuilder MapHub(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/api/admin/modules/hub").RequireAuthorization().RequireRateLimiting("authentication");
        admin.MapGet("", async (IMessageBus bus, CancellationToken ct) => Results.Ok(await bus.InvokeAsync<HubSettingsView>(new GetHubSettings(), ct)));
        admin.MapPut("", async (SaveHubSettings request, HubAdministrationService hub, CancellationToken ct) => Results.Ok(await hub.SaveAsync(request, ct)));
        admin.MapPost("/test", async (SaveHubSettings request, HubAdministrationService hub, HttpContext http, CancellationToken ct) =>
        {
            await hub.TestAsync(request, ct);
            return Results.Ok(new { ok = true, message = ErrorTexts.Translate(RequestLanguage.Of(http), "Spojení i oprávnění čtecího účtu fungují.") });
        });
        admin.MapGet("/groups/search", async (string? query, HubAdministrationService hub, CancellationToken ct) => Results.Ok(await hub.SearchAsync(query, ct)));
        admin.MapPost("/groups", async (AttachHubGroup request, HubAdministrationService hub, CancellationToken ct) => Results.Ok(await hub.AttachAsync(request, ct)));
        admin.MapPut("/groups/{groupId:guid}", async (Guid groupId, UpdateHubBinding request, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<HubSettingsView>(new ChangeHubBinding(groupId, request), ct)));
        admin.MapPost("/verify", async (VerifyHubIdentities request, HubAdministrationService hub, CancellationToken ct) => Results.Ok(await hub.VerifyAsync(request.UserId, false, ct)));
        admin.MapGet("/migration/inventory", async (IMessageBus bus, CancellationToken ct) => Results.Ok(await bus.InvokeAsync<HubMigrationInventory>(new ReadHubMigrationInventory(), ct)));
        admin.MapPost("/migration/preview", async (PreviewHubMigration request, HubMigrationService migration, CancellationToken ct) => Results.Ok(await migration.PreviewAsync(request, ct)));
        admin.MapPost("/migration/apply", async (ApplyHubMigration request, IMessageBus bus, CancellationToken ct) => Results.Ok(await bus.InvokeAsync<HubMigrationResult>(request, ct)));
        endpoints.MapGet("/api/profile/hub", async (IMessageBus bus, CancellationToken ct) =>
        {
            return Results.Ok(await bus.InvokeAsync<HubProfileView>(new GetHubProfile(), ct));
        }).RequireAuthorization();
        endpoints.MapPost("/api/profile/hub/verify", async (HubAdministrationService hub, CancellationToken ct) =>
        {
            return Results.Ok(await hub.VerifyAsync(null, true, ct));
        }).RequireAuthorization().RequireRateLimiting("authentication");
        return endpoints;
    }
}
