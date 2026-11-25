using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.PersonalLibrary.Commands;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Queries;
using Wolverine;

namespace Atlas.Server.Features.PersonalLibrary;

public static class PersonalLibraryEndpoints
{
    public static void MapPersonalLibraryEndpoints(this WebApplication app)
    {
        var analytics = app.MapGroup("/api/analytics").RequireAuthorization();
        analytics.MapGet("/activity", async (DateOnly from, DateOnly to, string scope, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<ActivitySummary>(new GetActivity(from, to, scope), ct)));
        analytics.MapGet("/changes", async (DateOnly day, string scope, int? offset, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<ActivityChanges>(new GetActivityChanges(day, scope, offset ?? 0), ct)));
        analytics.MapGet("/pages/{id:guid}", async (Guid id, DateOnly from, DateOnly to, int? offset, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PageReadership>(new GetPageReadership(id, from, to, offset ?? 0), ct)));
        analytics.MapGet("/popular", async (DateOnly from, DateOnly to, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PopularPages>(new GetPopularPages(from, to), ct)));
        MapRoutes(app.MapGroup("/api").RequireAuthorization(), false);
        MapRoutes(app.MapGroup("/api/v1").WithGroupName("v1")
            .RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
    }

    private static void MapRoutes(RouteGroupBuilder group, bool personalApi)
    {
        var list = group.MapGet("/me/library/{kind}", async (string kind, int? offset, int? limit, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PersonalLibraryList>(new ListPersonalLibrary(kind, offset ?? 0, limit ?? 20), ct)));
        var state = group.MapGet("/pages/{id:guid}/personal-state", async (Guid id, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PersonalPageState>(new GetPersonalPageState(id), ct)));
        var favorite = group.MapPut("/pages/{id:guid}/favorite", async (Guid id, SetFavorite request, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PersonalPageState>(new SetPageFavorite(id, request), ct)));
        var visit = group.MapPost("/pages/{id:guid}/visit", async (Guid id, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PersonalPageState>(new RecordPageVisit(id), ct)));
        var changes = group.MapGet("/me/changes", async (string? filter, string? cursor, int? limit, Guid? author, Guid? space, IMessageBus bus, CancellationToken ct) =>
                Results.Ok(await bus.InvokeAsync<RecentChangeList>(new ListRecentChanges(filter ?? "all", cursor, limit ?? 10, author, space), ct)))
            .Produces<RecentChangeList>().ProducesProblem(400)
            .WithSummary("Co se změnilo: the pages the caller may read changed in the last 30 days, newest change first, one row per page with its newest " +
                "version (author, channel, AI, token for its owner) and how many further versions. filter: all, ai or mine; author: only the changes of this account " +
                "(a person's activity, a contact's directoryId; no 30-day window); space: only the pages of this space; limit 1–50; cursor: next of the previous page.");
        list.Produces<PersonalLibraryList>().ProducesProblem(400)
            .WithSummary("List the owner's accessible library. kind: favorites, recent, drafts, updates or changes; offset 0–1000, limit 1–50.");
        state.Produces<PersonalPageState>().ProducesProblem(404).WithSummary("Read the owner's favorite and visit state for an accessible page.");
        favorite.Produces<PersonalPageState>().ProducesProblem(404).WithSummary("Set a personal favorite without changing the article or its permissions.");
        visit.Produces<PersonalPageState>().ProducesProblem(404).WithSummary("Record a personal visit to an accessible page.");
        if (personalApi)
        {
            list.WithName("ListPersonalLibrary");
            changes.WithName("ListRecentChanges");
            state.WithName("GetPersonalPageState");
            favorite.WithName("SetPersonalFavorite");
            visit.WithName("RecordPersonalVisit");
            // Personal state, not content: nothing readers see changes.
            favorite.RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.Favorite);
            visit.RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.Visit);
        }
    }
}
