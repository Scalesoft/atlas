using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.PersonalApi;
using Wolverine;

namespace Atlas.Server.Features.PageDecorations;

public static class PageDecorationsEndpoints
{
    public static void MapPageDecorations(this WebApplication app)
    {
        MapRoutes(app.MapGroup("/api").RequireAuthorization(), false);
        MapRoutes(app.MapGroup("/api/v1").WithGroupName("v1")
            .RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
    }

    private static void MapRoutes(RouteGroupBuilder group, bool personalApi)
    {
        var read = group.MapGet("/pages/{id:guid}/decoration", async (Guid id, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PageDecorationState>(new GetPageDecoration(id), ct)));
        var set = group.MapPut("/pages/{id:guid}/decoration", async (Guid id, SetPageDecorationRequest request, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PageDecorationState>(new SetPageDecoration(id, request), ct)));
        if (!personalApi) return;
        read.WithName("GetPageDecoration").Produces<PageDecorationState>().ProducesProblem(404)
            .WithSummary("Get the icon and colour of a page (decoration is null when it has none) with the revisionId to pass as expectedRevisionId.");
        set.WithName("SetPageDecoration").Produces<PageDecorationState>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(409)
            .WithSummary("Set or clear (icon and color both null) the icon and colour of a page. Takes effect for readers at once, is not published and creates no document revision. Send the revisionId you read as expectedRevisionId (null when the page has no decoration); a stale value is 409 decoration_changed. Needs the write scope.")
            .RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.PageDecorationSet);
        group.MapGet("/appearance", (string? q, int? limit, int? offset) => Results.Ok(AppearanceCatalogView.Read(q, limit, offset)))
            .WithName("GetAppearanceCatalog").Produces<AppearanceCatalogDto>().ProducesProblem(400)
            .WithSummary("The icon ids (filter q = part of an id, window limit/offset; iconTotal counts all matches) and the colour palette a page decoration accepts. Use these identifiers verbatim.");
    }
}
