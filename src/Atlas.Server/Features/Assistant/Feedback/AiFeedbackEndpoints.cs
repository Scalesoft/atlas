using Atlas.Server.Features.Assistant.Feedback.Commands;
using Atlas.Server.Features.Assistant.Feedback.Contracts;
using Wolverine;

namespace Atlas.Server.Features.Assistant.Feedback;

public static class AiFeedbackEndpoints
{
    /// <summary>
    /// Feedback and reports (plan phase 3 Q5). A signed-in browser session only (a personal token or sharing session is 401),
    /// and no AiGate: these are the person's own data and work while AI is off or down.
    /// </summary>
    public static void MapFeedback(this IEndpointRouteBuilder app)
    {
        var messages = app.MapGroup("/api/ai/messages").RequireAuthorization();
        messages.MapPut("{id:guid}/feedback", async (Guid id, AiFeedbackRequest request, IMessageBus bus, CancellationToken ct) =>
            {
                await bus.InvokeAsync(new RateAiMessage(id, request), ct);
                return Results.NoContent();
            })
            .Produces(204).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        messages.MapPost("{id:guid}/report-missing", async (Guid id, IMessageBus bus, CancellationToken ct) =>
            {
                await bus.InvokeAsync(new ReportMissingContent(id), ct);
                return Results.NoContent();
            })
            .Produces(204).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/search/report-missing", async (ReportMissingSearchRequest request, IMessageBus bus, CancellationToken ct) =>
            {
                await bus.InvokeAsync(new ReportMissingSearch(request), ct);
                return Results.NoContent();
            })
            .RequireAuthorization()
            .RequireRateLimiting("interactive-search")
            .Produces(204).ProducesProblem(400);
    }
}
