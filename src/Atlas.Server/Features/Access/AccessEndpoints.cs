using Atlas.Server.Features.Access.Commands;
using Atlas.Server.Features.Access.Contracts;
using Atlas.Server.Features.Access.Queries;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Platform.Localization;
using Wolverine;

namespace Atlas.Server.Features.Access;

/// <summary>The security centre's access layer (docs/access-model.md §3). Every response is no-store: it says who reaches what.</summary>
public static class AccessEndpoints
{
    public static void MapAccess(this IEndpointRouteBuilder app)
    {
        var access = app.MapGroup("/api/access").RequireAuthorization().AddEndpointFilter(NoStore);
        access.MapGet("/kinds", async (HttpContext http, IMessageBus bus, CancellationToken ct) =>
        {
            // The names and level descriptions are Czech in the kinds; the request's language translates them here (the Czech text is the key).
            var language = RequestLanguage.Of(http);
            var kinds = await bus.InvokeAsync<IReadOnlyList<AccessKindView>>(new GetAccessKinds(), ct);
            return Results.Ok(kinds.Select(kind => kind with
            {
                One = ErrorTexts.Translate(language, kind.One),
                Many = ErrorTexts.Translate(language, kind.Many),
                Levels = kind.Levels.Select(level => level with { Description = ErrorTexts.Translate(language, level.Description) }).ToList()
            }));
        });
        access.MapGet("/me", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<PrincipalAccess>(new GetMyAccess(), ct)));
        access.MapGet("/principals/{kind}/{id:guid}", async (string kind, Guid id, Guid? spaceId, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<PrincipalAccess>(new GetPrincipalAccess(kind, id, spaceId), ct)));
        access.MapGet("/principals/user/{id:guid}/revoke-all", async (Guid id, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<RevokeAllImpact>(new GetRevokeAllImpact(id), ct)));
        access.MapPost("/principals/user/{id:guid}/revoke-all", async (Guid id, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new RevokeAllAccess(id), ct);
            return Results.NoContent();
        });
        access.MapGet("/resources", async (string? kind, string? q, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<IReadOnlyList<AccessResource>>(new SearchAccessResources(kind, q), ct)));
        access.MapGet("/resources/{kind}/{id}", async (string kind, string id, string? view, string? q, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<ResourceAccess>(new GetResourceAccess(kind, id, view, q), ct)));
        access.MapPut("/resources/{kind}/{id:guid}/grants", async (string kind, Guid id, UpdateAccessGrant request, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetAccessGrant(kind, id, request), ct);
            return Results.NoContent();
        });
        access.MapDelete("/resources/{kind}/{id:guid}/grants/{subjectKind}/{subjectId:guid}", async (string kind, Guid id, string subjectKind,
            Guid subjectId, string? version, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new RevokeAccessGrant(kind, id, subjectKind, subjectId, version), ct);
            return Results.NoContent();
        });
        access.MapGet("/candidates", async (string? kind, string? q, bool? exceptMe, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<IReadOnlyList<AccessCandidate>>(new GetAccessCandidates(kind, q, exceptMe == true), ct)));
    }

    private static async ValueTask<object?> NoStore(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return await next(context);
    }
}
