using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Collaboration.Contracts;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Collaboration.Queries;
using Wolverine;

namespace Atlas.Server.Features.Collaboration;

public static class CollaborationEndpoints
{
    public static void MapCollaboration(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/pages/{id:guid}/collaboration", async (Guid id, IMessageBus bus, LiveRequirementsUpgrade upgrade, CancellationToken ct) =>
            {
                var session = await bus.InvokeAsync<WikiCollaborationSession>(new OpenCollaboration(id), ct);
                // After the open's commit (the document gate comes before the page's locks): a live state of a previous host schema.
                if (session.Upgraded)
                {
                    await upgrade.ConvertAsync(session.DocumentId, ct);
                }
                return Results.Ok(session);
            })
            .RequireAuthorization()
            .Produces<WikiCollaborationSession>()
            .ProducesProblem(404);
        // 3.6-I Živě: a reader of a live page joins the live document read-only; GET never starts or resets a generation.
        app.MapGet("/api/pages/{id:guid}/collaboration", async (Guid id, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<WikiCollaborationSession>(new WatchCollaboration(id), ct)))
            .RequireAuthorization()
            .Produces<WikiCollaborationSession>()
            .ProducesProblem(404);
        app.MapAtlasMarkdownSyncHub("/hubs/markdown").RequireAuthorization();
    }
}
