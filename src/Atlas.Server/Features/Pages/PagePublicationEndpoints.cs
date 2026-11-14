using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.PersonalApi;
using Wolverine;

namespace Atlas.Server.Features.Pages;

/// <summary>3.6-I: the page's „Publikace“ (Ručně, Automaticky, Živě) and the state of its publication, on the web and the personal API.</summary>
public static class PagePublicationEndpoints
{
    public static void MapPagePublication(this WebApplication app)
    {
        MapRoutes(app.MapGroup("/api").RequireAuthorization(), false);
        MapRoutes(app.MapGroup("/api/v1").WithGroupName("v1")
            .RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
    }

    private static void MapRoutes(RouteGroupBuilder group, bool personalApi)
    {
        var read = group.MapGet("/pages/{id:guid}/publication", async (Guid id, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PagePublicationState>(new GetPagePublication(id), ct)));
        var set = group.MapPut("/pages/{id:guid}/publication", async (Guid id, SetPagePublicationRequest request, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PagePublicationState>(new SetPagePublication(id, request), ct)));
        if (!personalApi) return;
        read.WithName("GetPagePublication").Produces<PagePublicationState>().ProducesProblem(404)
            .WithSummary("How the page is published: its own mode (Manual, Automatic, Live; null when it inherits spaceMode), the effective mode and the last publication (version, time, publisher, whether it was automatic, whether something was saved since).");
        set.WithName("SetPagePublication").Produces<PagePublicationState>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Set the page's publication mode: Manual (readers read what is published by hand), Automatic (a version is published by itself after 30 minutes without edits), Live (readers read the latest saved state live; versions are published as for Automatic); mode null inherits the space's. Automatic publication never shares a page that was never published. Needs edit rights on the page and the write scope.")
            .RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.PagePublicationSet);
    }
}
