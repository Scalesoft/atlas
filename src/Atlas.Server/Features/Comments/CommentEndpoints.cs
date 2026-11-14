using Atlas.Server.Features.Comments.Commands;
using Atlas.Server.Features.Comments.Contracts;
using Atlas.Server.Features.Comments.Queries;
using Wolverine;

namespace Atlas.Server.Features.Comments;

/// <summary>The comments of an object (comments spec). Cookie API: CSRF and Problem Details come from the pipeline, the rules from
/// the handlers.</summary>
public static class CommentEndpoints
{
    public static void MapComments(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/objects/{objectId:guid}/comments", async (Guid objectId, Guid? before, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<CommentPage>(new GetComments(objectId, before), ct)))
            .RequireAuthorization()
            .Produces<CommentPage>()
            .ProducesProblem(404);
        app.MapGet("/api/objects/{objectId:guid}/comments/count", async (Guid objectId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<CommentCount>(new GetCommentCount(objectId), ct)))
            .RequireAuthorization()
            .Produces<CommentCount>()
            .ProducesProblem(404);
        // GET with the ids in the query: a board's cards, at most 100 (about 3.7 kB of URL).
        app.MapGet("/api/objects/comments/counts", async (string? ids, IMessageBus bus, CancellationToken ct) =>
            {
                var objectIds = new List<Guid>();
                foreach (var id in (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    objectIds.Add(Guid.TryParse(id, out var parsed) ? parsed : throw new ApiException(400, "Seznam objektů není platný.", "invalid_comment"));
                }
                return Results.Ok(await bus.InvokeAsync<CommentCounts>(new GetCommentCounts(objectIds), ct));
            })
            .RequireAuthorization()
            .Produces<CommentCounts>()
            .ProducesProblem(400);
        app.MapPost("/api/objects/{objectId:guid}/comments", async (Guid objectId, PostCommentRequest request, IMessageBus bus, CancellationToken ct) =>
            {
                var comment = await bus.InvokeAsync<CommentDto>(new PostComment(objectId, request.Body, request.Anchor), ct);
                return Results.Created($"/api/objects/{objectId}/comments/{comment.Id}", comment);
            })
            .RequireAuthorization()
            .Produces<CommentDto>(201)
            .ProducesProblem(400)
            .ProducesProblem(403)
            .ProducesProblem(404);
        app.MapPut("/api/objects/{objectId:guid}/comments/{commentId:guid}",
                async (Guid objectId, Guid commentId, EditCommentRequest request, IMessageBus bus, CancellationToken ct)
                    => Results.Ok(await bus.InvokeAsync<CommentDto>(new EditComment(objectId, commentId, request.Body), ct)))
            .RequireAuthorization()
            .Produces<CommentDto>()
            .ProducesProblem(400)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(409);
        app.MapDelete("/api/objects/{objectId:guid}/comments/{commentId:guid}", async (Guid objectId, Guid commentId, IMessageBus bus, CancellationToken ct) =>
            {
                await bus.InvokeAsync(new DeleteComment(objectId, commentId), ct);
                return Results.NoContent();
            })
            .RequireAuthorization()
            .Produces(204)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(409);
    }
}
