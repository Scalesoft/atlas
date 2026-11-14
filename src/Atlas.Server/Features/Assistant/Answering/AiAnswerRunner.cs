using Microsoft.Extensions.AI;
using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Assistant.Agent;
using Atlas.Server.Features.Assistant.Commands;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Prompts;
using Atlas.Server.Features.Assistant.Queries;
using Atlas.Server.Features.Assistant.Sources;
using Atlas.Server.Features.Search.Domain;
using Wolverine;

namespace Atlas.Server.Features.Assistant.Answering;

/// <summary>
/// The answer flow of the endpoints (plan phase 3 R1, §7), outside Wolverine like <c>SearchEndpoints.SearchAsync</c>: every
/// database step is one short transaction (<see cref="PrepareAiTurn"/>, <see cref="BeginAiCompletion"/>,
/// <see cref="ReleaseAiAnswer"/>) and the only model call happens between them, outside any transaction and lock. Each step
/// starts with <see cref="AiGate"/> (AI-25: an unavailable task is 503 <c>ai_unavailable</c> before anything is written). A
/// provider outage during the call ends the answer <c>unavailable</c> with the same 503, a model that answers unusably or a
/// busy chat (no slot within 10 s) <c>failed</c> with 502 <c>ai_failed</c>, a client that goes away <c>canceled</c>.
/// Nothing here logs a question, a source or an answer; the pipeline logs only the task, the profile kind, the host and a code.
/// Clients other than the web (AI-11) call this class the same way; it does not depend on any UI.
/// </summary>
public sealed class AiAnswerRunner(IMessageBus bus, AiGate gate, AiConfigurationCache configuration, ISemanticQuery semantic,
    IAiClientFactory factory, TimeProvider clock, AiCompletionGate completions, AssistantAgentRunner agent, AnswerRerank rerank,
    ILogger<AiAnswerRunner> logger)
{
    /// <summary>Bounds structured answers, including an explicitly requested detailed answer of up to twelve sentences.</summary>
    public const int MaxOutputTokens = 3600;
    private const int SummaryMaxOutputTokens = 1200;
    /// <summary>AI4-10: a grounded answer decodes almost greedily, so the same question over the same sources answers alike.</summary>
    public const float GroundedTemperature = 0.1f;

    /// <summary>The admin's temperature wins; Anthropic gets none (Claude 5.x refuses non-default sampling).</summary>
    public static float? AnswerTemperature(AiProfileSnapshot profile)
        => profile.Temperature ?? (profile.Provider == AiProvider.Anthropic ? null : GroundedTemperature);

    /// <summary><c>POST /api/ai/conversations</c>: a new conversation (omnisearch: <c>search.answer</c>, not listed until
    /// "Pokračovat v asistentovi"; assistant: <c>chat.answer</c> or, asked for, <c>assistant.agent</c>, listed).</summary>
    public Task<AiTurnPrepared> StartAsync(StartAiConversationRequest request, CancellationToken ct)
    {
        if (request.Origin is not (AiConversationOrigin.Search or AiConversationOrigin.Assistant))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "Neznámý původ konverzace.");
        }
        var task = request.Origin == AiConversationOrigin.Search ? AiTaskCatalog.SearchAnswer : AssistantTask(request.Task);
        return PrepareAsync(null, request.Origin!, task, request.Question, request.Scope, request.Effort, ct);
    }

    /// <summary><c>POST /api/ai/conversations/{id}/messages</c>: a follow-up, always an assistant's task.</summary>
    public Task<AiTurnPrepared> AskAsync(Guid conversationId, AskAiRequest request, CancellationToken ct)
        => PrepareAsync(conversationId, AiConversationOrigin.Assistant, AssistantTask(request.Task), request.Question, request.Scope, request.Effort, ct);

    /// <summary>D14: the panel asks for the agent with tools or the answer from the wiki (the default); the right is the gate's (D17).</summary>
    private static string AssistantTask(string? task) => task switch
    {
        null or AiTaskCatalog.ChatAnswer => AiTaskCatalog.ChatAnswer,
        AiTaskCatalog.AssistantAgent => AiTaskCatalog.AssistantAgent,
        _ => throw new ApiException(StatusCodes.Status400BadRequest, "Neznámá úloha asistenta."),
    };

    /// <summary>A page summary uses the same completion and revocation protocol, with deterministic section preparation.</summary>
    public async Task<AiTurnPrepared> SummaryAsync(PageSummaryRequest request, CancellationToken ct)
    {
        await gate.RequireAsync(AiTaskCatalog.PageSummary, ct);
        var snapshot = await configuration.GetAsync(ct);
        var profile = snapshot.ProfileFor(AiTaskCatalog.PageSummary) ?? throw AiGate.Unavailable();
        return await bus.InvokeAsync<AiTurnPrepared>(new PrepareAiTurn(null, AiConversationOrigin.Page, AiTaskCatalog.PageSummary,
            "Shrnutí stránky", new AiScope(AiScopeKind.Page, null, request.PageId), null, profile.RevisionId,
            ChatProfileCompatibility.Fingerprint(profile), profile.Model, snapshot.DailyLimit(AiTaskCatalog.PageSummary)), ct);
    }

    private async Task<AiTurnPrepared> PrepareAsync(Guid? conversationId, string origin, string task, string? question, AiScope? scope,
        string? effort, CancellationToken ct)
    {
        var text = ValidQuestion(question);
        var validScope = ValidScope(scope);
        var validEffort = AnswerEffort.Parse(effort);
        await gate.RequireAsync(task, ct);
        var snapshot = await configuration.GetAsync(ct);
        var profile = snapshot.ProfileFor(task) ?? throw AiGate.Unavailable();
        // The question's vector before the transaction, within 1 s (R3); a vector the omnisearch already made comes from the cache.
        // An agent turn retrieves nothing at preparation (its tools search), so it needs none.
        var vector = task == AiTaskCatalog.AssistantAgent ? null : await semantic.TryEmbedAsync(text, SemanticQueryService.AnswerBudget, ct);
        var preferred = task == AiTaskCatalog.AssistantAgent ? null : await RerankSourcesAsync(text, validScope, vector, validEffort, ct);
        return await bus.InvokeAsync<AiTurnPrepared>(new PrepareAiTurn(conversationId, origin, task, text, validScope, vector, profile.RevisionId,
            ChatProfileCompatibility.Fingerprint(profile), profile.Model, snapshot.DailyLimit(task), validEffort) { Preferred = preferred }, ct);
    }

    /// <summary>The rerank step of an answer's sources, before the preparation transaction: the pool in its own short read, scored within
    /// <see cref="AnswerRerank.Budget"/>. Null (today's retrieval, no extra read) without a reranker, over its budget or after a failure.</summary>
    private async Task<IReadOnlyList<SourceReference>?> RerankSourcesAsync(string question, AiScope scope, SemanticQuery? vector, AnswerEffort effort,
        CancellationToken ct)
    {
        if (!await rerank.AvailableAsync(ct))
        {
            return null;
        }
        var pool = (await bus.InvokeAsync<AnswerCandidates>(new RetrieveAnswerCandidates(question, scope, vector, effort), ct)).Sources;
        var order = await rerank.OrderAsync(question, pool.Select(source => AnswerRerank.Document(source.Title, source.HeadingTitle, source.Text)).ToArray(), ct);
        return order?.Select(index => pool[index].Reference).ToArray();
    }

    /// <summary><c>POST /api/ai/messages/{id}/complete</c>: the check before the model, the model call, the validation of
    /// citations and the revocation gate. Stopping is aborting this request. An agent turn runs its tools in
    /// <see cref="AssistantAgentRunner"/> and reports each call to <paramref name="onStep"/> (the NDJSON stream, spec S6).</summary>
    public async Task<AiMessageView> CompleteAsync(Guid messageId, Func<AgentStepEvent, CancellationToken, Task>? onStep, CancellationToken ct)
    {
        var task = (await bus.InvokeAsync<AiMessageTask>(new GetAiMessageTask(messageId), ct)).Task;
        using var completion = completions.Enter(messageId);
        if (task == AiTaskCatalog.AssistantAgent)
        {
            // The tools right (D17) again: it may have been taken away since the preparation.
            await gate.RequireAsync(task, ct);
        }
        else
        {
            await gate.RequireGlobalAsync(task, ct);
        }
        var profile = (await configuration.GetAsync(ct)).ProfileFor(task) ?? throw AiGate.Unavailable();
        var start = await bus.InvokeAsync<AiCompletionStart>(new BeginAiCompletion(messageId, ChatProfileCompatibility.Fingerprint(profile)), ct);
        if (start.Finished is { } finished)
        {
            return finished;
        }
        var input = start.Input!;
        if (task == AiTaskCatalog.AssistantAgent)
        {
            return await agent.RunAsync(messageId, input, profile, onStep, ct);
        }
        var started = clock.GetTimestamp();
        AnswerCompletion completed;
        try
        {
            using var chat = factory.Chat(profile, task);
            completed = await AnswerOutput.GetAsync(chat, task == AiTaskCatalog.PageSummary ? SummaryPrompt.Build(input.Sources) : AnswerPromptBuilder.Build(input.Question, input.Sources, input.History),
                input.Sources.Select(source => source.PageTitle).ToArray(),
                new ChatOptions { MaxOutputTokens = task == AiTaskCatalog.PageSummary ? SummaryMaxOutputTokens : MaxOutputTokens, Temperature = AnswerTemperature(profile) }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await AbandonAsync(messageId, AiMessageState.Canceled, null);
            throw;
        }
        catch (AiCallException failure) when (AiOutcome.IsAvailabilityFailure(failure.Code))
        {
            // A provider outage, even the first failure (§5.3 "Nedostupné"): the neutral sentence, and nothing is kept.
            await AbandonAsync(messageId, AiMessageState.Unavailable, failure.Code);
            throw AiGate.Unavailable();
        }
        catch (AiCallException failure)
        {
            await AbandonAsync(messageId, AiMessageState.Failed, failure.Code);
            throw AiAnswerProblems.CouldNotPrepare();
        }
        var latency = (int)Math.Clamp(Math.Round(clock.GetElapsedTime(started).TotalMilliseconds), 0, int.MaxValue);
        var (result, response, retried) = completed;
        // P2-14: one line per answer, so the share of retried outputs (a provider ignoring the schema) is countable.
        logger.LogInformation("Answer output of {Task} on {Model}: retried {Retried}, finish {FinishReason}", task, profile.Model, retried, response.FinishReason?.Value);
        if (result is null)
        {
            await AbandonAsync(messageId, AiMessageState.Failed, AiOutcome.InvalidOutput);
            throw AiAnswerProblems.CouldNotPrepare();
        }
        var validated = AnswerValidator.Validate(result, input.Sources.Count);
        var model = string.IsNullOrWhiteSpace(response.ModelId) ? profile.Model : response.ModelId;
        var metrics = new AiAnswerMetrics(task == AiTaskCatalog.PageSummary ? SummaryPrompt.Version : AnswerPrompt.Version, profile.RevisionId, model.Length > 200 ? model[..200] : model,
            ToInt(response.Usage?.InputTokenCount), ToInt(response.Usage?.OutputTokenCount), latency);
        try
        {
            return await bus.InvokeAsync<AiMessageView>(new ReleaseAiAnswer(messageId, validated, metrics, AiProvenanceView.From(profile, metrics.Model)), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await AbandonAsync(messageId, AiMessageState.Canceled, null);
            throw;
        }
    }

    /// <summary>Always completes, even after the request was aborted: the answer must not stay "preparing".</summary>
    private Task AbandonAsync(Guid messageId, string state, string? errorCode)
        => bus.InvokeAsync(new AbandonAiTurn(messageId, state, errorCode), CancellationToken.None);

    private static int? ToInt(long? value) => value is null ? null : (int)Math.Clamp(value.Value, 0, int.MaxValue);

    private static string ValidQuestion(string? question)
    {
        var text = question?.Trim() ?? "";
        if (text.Length == 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "Napište otázku.");
        }
        if (text.Length > AiConversationLimits.QuestionMaxLength)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "Otázka může mít nejvýše 2 000 znaků.");
        }
        return text;
    }

    private static AiScope ValidScope(AiScope? scope)
        => scope?.Kind switch
        {
            null or AiScopeKind.Wiki => new AiScope(AiScopeKind.Wiki, null, null),
            AiScopeKind.Space when scope.SpaceId is { } space => new AiScope(AiScopeKind.Space, space, null),
            AiScopeKind.Page when scope.PageId is { } page => new AiScope(AiScopeKind.Page, null, page),
            _ => throw new ApiException(StatusCodes.Status400BadRequest, "Neplatný rozsah otázky."),
        };
}
