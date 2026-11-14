using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Assistant.Agent;
using Atlas.Server.Features.Assistant.Answering;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Feedback;

namespace Atlas.Server.Features.Assistant;

public static class AssistantEndpoints
{
    /// <summary>The rate limit of a preparation (plan phase 3 §3): a token bucket per account, 10 tokens, one every 6 s.</summary>
    public const string AnswerRateLimit = "ai-answer";

    /// <summary>
    /// <c>/api/ai</c> of the assistant (plan phase 3 §7, §8, §9): a signed-in browser session only, never a personal token or a
    /// sharing session. Each package owns its own method and adds one call here: MapAnswers (Q3), MapConversations (Q4) and
    /// MapFeedback (Q5).
    /// </summary>
    public static void MapAssistant(this IEndpointRouteBuilder app)
    {
        app.MapAnswers();
        app.MapPost("/api/assistant/summary", async (PageSummaryRequest request, AiAnswerRunner runner, CancellationToken ct)
                => Results.Ok(await runner.SummaryAsync(request, ct)))
            .RequireAuthorization().RequireRateLimiting(AnswerRateLimit).Produces<AiTurnPrepared>();
        app.MapConversations();
        app.MapFeedback();
    }

    /// <summary>
    /// Q3, the two steps of an answer (R1): the preparation (a new conversation or a follow-up; rate limit <c>ai-answer</c>) answers
    /// <see cref="AiTurnPrepared"/>, the completion the finished <see cref="AiMessageView"/>. 503 <c>ai_unavailable</c> when the task
    /// is unavailable or the provider fails, 429 <c>ai_limit</c> over the daily limit, 502 <c>ai_failed</c> for an unusable answer or
    /// a busy chat, 409 <c>turn_in_progress</c>, <c>conversation_unavailable</c>, <c>conversation_outdated</c>, <c>turn_finished</c>,
    /// 404 for a scope, conversation or message the actor may not see. The preparation takes <c>task</c> <c>assistant.agent</c>
    /// (403 <c>ai_tools_forbidden</c> without the right, D17). The completion with <c>Accept: application/x-ndjson</c> streams
    /// (spec S6, <c>agentStream.ts</c>): a <c>step</c> line per tool start and end, a <c>proposal</c> line when the turn proposed a
    /// write (<see cref="AiProposalView"/>), then one <c>message</c> line, or an <c>error</c> line once a line was written; a problem
    /// before the first line is the usual Problem Details.
    /// Proposals (spec §6.1, WP-20): <c>GET /api/ai/proposals/{id}</c> reopens the owner's card, <c>POST …/decision</c>
    /// <see cref="DecideAiProposalRequest"/> declines or runs the stored call with the chosen items and answers the decided
    /// <see cref="AiProposalView"/>: 404 for anybody else's, 403 <c>ai_tools_forbidden</c> without the tools right, 409
    /// <c>proposal_expired</c> / <c>proposal_superseded</c> / <c>proposal_decided</c>, 400 for no or unknown items.
    /// </summary>
    private static void MapAnswers(this IEndpointRouteBuilder app)
    {
        var answers = app.MapGroup("/api/ai").RequireAuthorization();
        answers.MapPost("/conversations", async (StartAiConversationRequest request, AiAnswerRunner runner, CancellationToken ct)
                => Results.Ok(await runner.StartAsync(request, ct)))
            .RequireRateLimiting(AnswerRateLimit)
            .Produces<AiTurnPrepared>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);
        answers.MapPost("/conversations/{id:guid}/messages", async (Guid id, AskAiRequest request, AiAnswerRunner runner, CancellationToken ct)
                => Results.Ok(await runner.AskAsync(id, request, ct)))
            .RequireRateLimiting(AnswerRateLimit)
            .Produces<AiTurnPrepared>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);
        answers.MapGet("/proposals/{id:guid}", async (Guid id, AssistantProposals proposals, CancellationToken ct)
                => Results.Ok(await proposals.GetAsync(id, ct)))
            .Produces<AiProposalView>().ProducesProblem(404);
        answers.MapPost("/proposals/{id:guid}/decision", async (Guid id, DecideAiProposalRequest request, AssistantProposals proposals, CancellationToken ct)
                => Results.Ok(await proposals.DecideAsync(id, request, ct)))
            .Produces<AiProposalView>()
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        answers.MapPost("/messages/{id:guid}/complete", async (Guid id, AiAnswerRunner runner, AssistantProposals proposals, HttpContext context,
            CancellationToken ct) =>
            {
                if (!context.Request.GetTypedHeaders().Accept.Any(accept => accept.MediaType.Equals(AgentStream.MediaType, StringComparison.OrdinalIgnoreCase)))
                {
                    return Results.Ok(await runner.CompleteAsync(id, null, ct));
                }
                var stream = new AgentStream(context.Response);
                AiMessageView message;
                try
                {
                    message = await runner.CompleteAsync(id, stream.StepAsync, ct);
                }
                catch (ApiException error) when (stream.Started)
                {
                    await stream.ErrorAsync(error, ct);
                    return Results.Empty;
                }
                if (message.ProposalId is { } proposalId)
                {
                    await stream.ProposalAsync(await proposals.GetAsync(proposalId, ct), ct);
                }
                await stream.MessageAsync(message, ct);
                return Results.Empty;
            })
            .Produces<AiMessageView>()
            .Produces(StatusCodes.Status200OK, contentType: AgentStream.MediaType)
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(502).ProducesProblem(503);
    }

    /// <summary>
    /// The NDJSON of an agent turn (spec S6): one JSON object per line, flushed at once. The response starts with the first line, so
    /// a failure before it (404, 409, 503 before the model) stays the usual Problem Details. Steps carry the tool and a display
    /// name, never an argument or a tool output. The message line is the view the JSON response would have been.
    /// </summary>
    private sealed class AgentStream(HttpResponse response)
    {
        public const string MediaType = "application/x-ndjson";
        private readonly JsonSerializerOptions m_json = response.HttpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;

        public bool Started { get; private set; }

        public Task StepAsync(AgentStepEvent step, CancellationToken ct)
        {
            var line = new JsonObject { ["type"] = "step", ["id"] = step.Id, ["phase"] = step.Phase, ["tool"] = step.Tool };
            if (step.Target is { } target) line["target"] = target;
            if (step.Ok is { } ok) line["ok"] = ok;
            return WriteAsync(line, ct);
        }

        public Task ProposalAsync(AiProposalView proposal, CancellationToken ct)
            => WriteAsync(new JsonObject { ["type"] = "proposal", ["proposal"] = JsonSerializer.SerializeToNode(proposal, m_json) }, ct);

        public Task MessageAsync(AiMessageView message, CancellationToken ct)
            => WriteAsync(new JsonObject { ["type"] = "message", ["message"] = JsonSerializer.SerializeToNode(message, m_json) }, ct);

        public Task ErrorAsync(ApiException error, CancellationToken ct)
            => WriteAsync(new JsonObject { ["type"] = "error", ["code"] = error.Code ?? AiAnswerProblems.Failed, ["status"] = error.Status }, ct);

        private async Task WriteAsync(JsonObject line, CancellationToken ct)
        {
            if (!Started)
            {
                Started = true;
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = MediaType;
                response.Headers.CacheControl = "no-store";
            }
            await response.Body.WriteAsync(Encoding.UTF8.GetBytes(line.ToJsonString(m_json) + "\n"), ct);
            await response.Body.FlushAsync(ct);
        }
    }
}
