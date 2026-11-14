using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Access;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.Ai.Settings.SavedProfiles;
using Wolverine;

namespace Atlas.Server.Features.Ai;

public static class AiEndpoints
{
    /// <summary>The capabilities of a signed-in session, and Administrace → AI. Every admin route is WikiAdmin only; the
    /// handlers decide it like the other module settings.</summary>
    public static void MapAi(this IEndpointRouteBuilder app)
    {
        app.MapGet(GetAiCapabilities.Route, GetAiCapabilities.HandleAsync).RequireAuthorization();
        app.MapGet("/api/profile/ai", async (HttpContext context, IMessageBus bus, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await bus.InvokeAsync<MyAiPolicyView>(new GetMyAiPolicy(), ct));
        }).RequireAuthorization();
        app.MapGet("/api/admin/users/{id:guid}/ai-policy", async (Guid id, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<UserAiPolicyView>(new GetUserAiPolicy(id), ct))).RequireAuthorization();
        app.MapPut("/api/admin/users/{id:guid}/ai-policy", async (Guid id, SaveUserAiPolicyRequest request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<UserAiPolicyView>(new SaveUserAiPolicy(id, request), ct))).RequireAuthorization();
        var admin = app.MapGroup("/api/admin/ai").RequireAuthorization();
        admin.MapSavedProfiles();
        admin.MapGet("/access", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<AiAccessView>(new GetAiAccess(), ct)));
        admin.MapPut("/access", async (SaveAiAccessRequest request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<AiAccessView>(new SaveAiAccess(request), ct)));
        admin.MapGet("/", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<AiOverview>(new GetAiOverview(), ct)));
        admin.MapGet("/switch", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<AiSwitchView>(new GetAiSwitch(), ct)));
        admin.MapPut("/switch", async (SaveAiSwitchRequest request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<AiSwitchView>(new SaveAiSwitch(request), ct)));
        admin.MapGet("/profiles/{kind}", async (string kind, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<AiProfileView>(new GetAiProfile(kind), ct)));
        admin.MapPut("/profiles/{kind}", async (string kind, SaveAiProfileRequest request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<AiProfileView>(new SaveAiProfile(kind, request), ct)));
        admin.MapPost("/profiles/{kind}/models", async (string kind, DiscoverAiModelsRequest request, DiscoverAiModelsRunner runner, CancellationToken ct)
            => Results.Ok(await runner.DiscoverAsync(kind, request, ct))).RequireRateLimiting("ai-admin-test");
        // A test spends tokens: 6 a minute per admin.
        admin.MapPost("/profiles/{kind}/test", async (string kind, TestAiProfileRunner runner, CancellationToken ct)
            => Results.Ok(await runner.TestAsync(kind, ct))).RequireRateLimiting("ai-admin-test");
        admin.MapGet("/features", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<AiFeaturesView>(new GetAiFeatures(), ct)));
        admin.MapPut("/features", async (SaveAiFeaturesRequest request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<AiFeaturesView>(new SaveAiFeatures(request), ct)));
    }
}
