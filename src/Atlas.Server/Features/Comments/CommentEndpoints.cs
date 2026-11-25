using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Comments.Commands;
using Atlas.Server.Features.Comments.Contracts;
using Atlas.Server.Features.Comments.Queries;
using Atlas.Server.Features.PersonalApi;
using Wolverine;

namespace Atlas.Server.Features.Comments;

/// <summary>The comments of an object (comments spec). Cookie API: CSRF and Problem Details come from the pipeline, the rules from
/// the handlers. For AI parity, reading and posting also under /api/v1 (read, and write scope for a post).</summary>
public static class CommentEndpoints
{
    public static void MapComments(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/objects/{objectId:guid}/comments", ReadAsync)
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
        app.MapPost("/api/objects/{objectId:guid}/comments", PostAsync)
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

        var v1 = app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api");
        v1.MapGet("/objects/{objectId:guid}/comments", ReadAsync)
            .WithName("GetComments")
            .Produces<CommentPage>()
            .ProducesProblem(404)
            .WithSummary("The object's (a page's, a board's, a contact's…) comments for whoever may read it: newest first, 20 per page; before = the id " +
                "of the oldest comment you have for the next page. count: its live comments; canPost: whether you may write one now (the space's comments on).");
        v1.MapPost("/objects/{objectId:guid}/comments", PostAsync)
            .WithName("PostComment")
            .RequireAuthorization(PersonalApiEndpoints.WritePolicy)
            .AuditApiWrite(ApiOperations.CommentPost)
            .Produces<CommentDto>(201)
            .ProducesProblem(400)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .WithSummary("Post a comment (Markdown, at most 10 000 characters) on an object you may read, in a space with comments on. The timeline " +
                "is flat: a reply is the next comment; a mention is a contact link [Name](a-contact:?internalUser.id=<userId>) and notifies that user. " +
                "anchor: optionally a heading or item id of the object.");
    }

    private static async Task<IResult> ReadAsync(Guid objectId, Guid? before, IMessageBus bus, CancellationToken ct)
        => Results.Ok(await bus.InvokeAsync<CommentPage>(new GetComments(objectId, before), ct));

    private static async Task<IResult> PostAsync(Guid objectId, PostCommentRequest request, HttpContext context, IMessageBus bus, CancellationToken ct)
    {
        var comment = await bus.InvokeAsync<CommentDto>(new PostComment(objectId, request.Body, request.Anchor), ct);
        // The same prefix the request came through: /api or /api/v1.
        return Results.Created($"{context.Request.PathBase}{context.Request.Path}/{comment.Id}", comment);
    }
}
