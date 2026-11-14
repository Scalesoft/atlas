using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.PageLinks.Contracts;
using Atlas.Server.Features.PageLinks.Queries;
using Wolverine;

namespace Atlas.Server.Features.PageLinks;

public static class PageLinksEndpoints
{
    public static void MapPageLinks(this IEndpointRouteBuilder app)
    {
        var pages = app.MapGroup("/api/pages").RequireAuthorization();
        // Related pages follow the page.related capability (memory only, A3); without it the field is absent and the rest is
        // unchanged, never a 503: links here are not an AI function.
        pages.MapGet("/{id:guid}/connections", async (Guid id, int? limit, AiCapabilities capabilities, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<PageConnections>(await GetPageConnections.ForReaderAsync(id, limit ?? 10, capabilities, ct), ct)))
            .Produces<PageConnections>()
            .ProducesProblem(400)
            .ProducesProblem(404);
        pages.MapGet("/{id:guid}/publication-check", async (Guid id, int expectedVersion, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<PublicationCheck>(new CheckPublication(id, expectedVersion), ct)))
            .Produces<PublicationCheck>()
            .ProducesProblem(404)
            .ProducesProblem(409);
        pages.MapPost("/references", async (PageReferencesRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<PageReferenceList>(new ResolvePageReferences(request.Ids!), ct)))
            .Produces<PageReferenceList>()
            .ProducesProblem(400);
    }
}
