using Atlas.Server.Features.Assistant.Commands;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Queries;
using Wolverine;

namespace Atlas.Server.Features.Assistant;

/// <summary>
/// Conversations of the assistant (plan phase 3 Q4, §8, §20): list, detail, Continue (S6) and deletion. A signed-in browser
/// session only (cookie; a personal token or a shared-page visitor is 401), without <c>AiGate</c>: the history is the user's data
/// and deleting it is the user's right, with AI off too (návrh §2a point 7). Every response is no-store: it is private.
/// </summary>
public static class ConversationEndpoints
{
    public static void MapConversations(this IEndpointRouteBuilder app)
    {
        var conversations = app.MapGroup("/api/ai/conversations").RequireAuthorization().AddEndpointFilter(NoStore);
        conversations.MapGet("", async (DateTimeOffset? before, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AiConversationList>(new ListAiConversations(before), ct)))
            .Produces<AiConversationList>();
        conversations.MapGet("/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AiConversationView>(new GetAiConversation(id), ct)))
            .Produces<AiConversationView>();
        conversations.MapPost("/{id:guid}/continue", async (Guid id, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AiConversationView>(new ContinueAiConversation(id), ct)))
            .Produces<AiConversationView>();
        conversations.MapDelete("/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new DeleteAiConversation(id), ct);
            return Results.NoContent();
        });
        conversations.MapDelete("", async (IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new DeleteAllAiConversations(), ct);
            return Results.NoContent();
        });
    }

    private static async ValueTask<object?> NoStore(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return await next(context);
    }
}
