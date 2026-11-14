using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.PersonalApi;
using Wolverine;

namespace Atlas.Server.Features.Collaboration;

/// <summary>
/// The HTTP routes that write a page's content without a live generation (the personal API, the web's linking), run as a live write of
/// their page (3.6-H, <see cref="LivePageWrites"/>): the page is the route's <c>pageId</c> or <c>id</c>, the version read is the body's
/// <c>expectedVersion</c> (or the query's). The MCP tools do the same in <c>McpLiveWrites</c>.
/// </summary>
public static class LiveWriteEndpoints
{
    /// <summary>Undo of one revision's change (the history's „Vrátit tuto změnu“ and the personal API alike): <see cref="UndoRevisionChange"/>.</summary>
    public static void MapLiveWrites(this IEndpointRouteBuilder app)
    {
        static Task<PageDto> Undo(Guid id, Guid revisionId, UndoRevisionChangeRequest request, IMessageBus bus, CancellationToken ct)
            => bus.InvokeAsync<PageDto>(new UndoRevisionChange(id, revisionId, request.ExpectedVersion), ct);
        app.MapPost("/api/pages/{id:guid}/history/{revisionId:guid}/undo", Undo).RequireAuthorization().LiveWrite()
            .Produces<PageDto>().ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api")
            .MapPost("/pages/{id:guid}/history/{revisionId:guid}/undo", Undo).RequireAuthorization(PersonalApiEndpoints.WritePolicy)
            .AuditApiWrite(ApiOperations.PageUndoChange).WithMetadata(PersonalApiConcurrency.MarkdownWrites).LiveWrite()
            .Produces<PageDto>().ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).WithName("UndoRevisionChange")
            .WithSummary("Undo one revision's change as a whole (e.g. an agent's write, which has a revision of its own): only the blocks that revision " +
                "changed go back, what was written since elsewhere stays. 409 change_not_revertible when those blocks changed since; then restore the whole " +
                "revision. Requires expectedVersion of the working revision; publishes nothing.");
    }

    public static RouteHandlerBuilder LiveWrite(this RouteHandlerBuilder route, bool wholeDocument = false)
        => route.AddEndpointFilter(async (context, next) =>
        {
            var values = context.HttpContext.Request.RouteValues;
            if (!Guid.TryParse((values["pageId"] ?? values["id"])?.ToString(), out var pageId))
            {
                return await next(context);
            }
            var version = context.Arguments.OfType<IExpectedVersion>().Select(request => (int?)request.ExpectedVersion).FirstOrDefault() ??(int.TryParse(context.HttpContext.Request.Query["expectedVersion"], out var query) ? query : null);
            return await context.HttpContext.RequestServices.GetRequiredService<LivePageWrites>()
                .RunAsync(pageId, version, wholeDocument, async () =>
                {
                    try
                    {
                        return await next(context);
                    }
                    // As MCP's DraftTools: placeholders ⟦c:N⟧ that people's typing since the read renumbered are a changed target (409, read again), not bad input.
                    catch (ApiException error) when (error.Code == "components_changed" && LivePageWriteScope.Current is { } live && live.ReadVersion < live.StartVersion)
                    {
                        throw LiveWriteTarget.Changed(live.WholeDocument);
                    }
                }, context.HttpContext.RequestAborted);
        });
}

/// <summary>A request body that names the page version it was read at: <see cref="LiveWriteEndpoints.LiveWrite"/> takes it as the live write's read version.</summary>
public interface IExpectedVersion
{
    int ExpectedVersion { get; }
}

public sealed record UndoRevisionChangeRequest(int ExpectedVersion) : IExpectedVersion;
