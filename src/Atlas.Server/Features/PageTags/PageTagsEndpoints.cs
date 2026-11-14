using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.Search.Contracts;
using Wolverine;
using Atlas.Server.Features.PageTags.Queries;

namespace Atlas.Server.Features.PageTags;

public static class PageTagsEndpoints
{
    public static void MapPageTags(this WebApplication app)
    {
        MapRoutes(app.MapGroup("/api").RequireAuthorization(), false);
        MapRoutes(app.MapGroup("/api/v1").WithGroupName("v1")
            .RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
    }

    private static void MapRoutes(RouteGroupBuilder group, bool personalApi)
    {
        var read = group.MapGet("/pages/{id:guid}/tags", async (Guid id, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PageTagState>(new GetPageTags(id), ct)));
        var set = group.MapPut("/pages/{id:guid}/tags", async (Guid id, SetPageTagsRequest request, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PageTagState>(new SetPageTags(id, request), ct)));
        var suggest = group.MapGet("/tags", async (string? q, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<TagSuggestions>(new SuggestTags(q), ct))).RequireRateLimiting("search");
        var pages = group.MapGet("/tags/{id:guid}/pages", async (Guid id, string? q, Guid? spaceId, string? content,
            string? changedWithin, string? sort, int? offset, int? limit, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<SearchResponse>(new ListTaggedPages(id, q, spaceId, offset ?? 0, limit ?? 20,
                new SearchOptions(content ?? "all", changedWithin ?? "any", sort ?? "relevance")), ct)))
            .RequireRateLimiting("search");
        if (!personalApi) return;
        const string Immediate = "Takes effect for readers at once, is not published and creates no document revision. Needs the write scope.";
        read.WithName("GetPageTags").Produces<PageTagState>().ProducesProblem(404)
            .WithSummary("Get the tags of a page (their ids are the expectedTagIds of a write).");
        set.WithName("SetPageTags").Produces<PageTagState>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(409)
            .WithSummary("Replace the whole tag set of a page by names (at most 20) using expectedTagIds; a stale set is 409 tags_changed. " + Immediate)
            .RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.PageTagsSet);
        group.MapPost("/pages/{id:guid}/tags/changes", async (Guid id, ChangePageTagsRequest request, IMessageBus bus, CancellationToken ct) =>
                Results.Ok(await bus.InvokeAsync<PageTagState>(new ChangePageTags(id, request), ct)))
            .WithName("ChangePageTags").Produces<PageTagState>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(409)
            .WithSummary("Add and remove tags by name and keep the page's other tags; applied to the current set under the page lock, so concurrent edits do not lose each other's tags. Optional expectedTagIds still gives 409 tags_changed. " + Immediate)
            .RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.PageTagsChange);
        suggest.WithName("SuggestTags").Produces<TagSuggestions>()
            .WithSummary("Existing tags (at most 20) whose name contains q, only from pages the token owner can read.");
        pages.WithName("ListTaggedPages").Produces<SearchResponse>().ProducesProblem(400)
            .WithSummary("Pages carrying a tag, readable by the token owner; optional spaceId, content (all|published|drafts), q, sort, offset and limit (1-50).");
    }
}
