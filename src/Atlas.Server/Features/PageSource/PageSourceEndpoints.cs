using Atlas.Server.Features.PageSource.Commands;
using Atlas.Server.Features.PageSource.Contracts;
using Wolverine;

namespace Atlas.Server.Features.PageSource;

public static class PageSourceEndpoints
{
    public static void MapPageSource(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/pages/{id:guid}").RequireAuthorization()
            .MapPut("/source", async (Guid id, ReplacePageSourceRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<PageDto>(new ReplacePageSource(id, request), ct)))
            .Produces<PageDto>()
            .ProducesProblem(400)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(409);
    }
}
