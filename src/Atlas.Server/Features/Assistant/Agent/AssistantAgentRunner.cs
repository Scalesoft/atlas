using System.Text.Json;
using Microsoft.Extensions.AI;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Assistant.Commands;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Prompts;
using Atlas.Server.Platform.Modules;
using ModelContextProtocol;
using Wolverine;

namespace Atlas.Server.Features.Assistant.Agent;

/// <summary>
/// One step event of a running agent turn (the NDJSON <c>step</c> line, <c>agentStream.ts</c>): <see cref="Id"/> joins the start and
/// the end, <see cref="Target"/> is a display name a tool result of this turn gave (never an argument value or content),
/// <see cref="Ok"/> only on the end.
/// </summary>
public sealed record AgentStepEvent(string Id, string Phase, string Tool, string? Target, bool? Ok);

/// <summary>
/// The model part of an agent turn (<c>assistant.agent</c>, spec §4.1, WP-14), called by <see cref="Answering.AiAnswerRunner"/> between
/// <see cref="BeginAiCompletion"/> and the release, outside any transaction and lock: the router picks tool groups from the question,
/// the earlier questions and the question's scope (<see cref="AssistantToolGroups"/>), <see cref="AssistantToolSession"/> runs them as
/// the signed-in user, and a <see cref="FunctionInvokingChatClient"/> over the profile of <c>assistant.agent</c> calls them, at most
/// <see cref="MaxToolRounds"/> rounds within <see cref="TurnTimeout"/>. The pipeline records <c>AiUsage</c> for every model call. Each
/// tool call reports a step start and end (<see cref="StepFunction"/>). The answer, its sources (only pages the tools returned,
/// <see cref="AgentAnswer"/>), the steps and the turn's write proposal (<see cref="AssistantProposals"/>) are released by <see cref="ReleaseAgentAnswer"/>. Nothing here logs a question, an argument,
/// a tool output or an answer; the function-invoking client gets no logger, since it would log arguments at trace level.
/// </summary>
public sealed class AssistantAgentRunner(IMessageBus bus, IAiClientFactory factory, IHttpContextAccessor http, TimeProvider clock,
    AssistantProposals proposals, AiCapabilities capabilities)
{
    public const int MaxToolRounds = 8;
    public const int MaxOutputTokens = 3600;
    /// <summary>The hits of the turn's starting search; their passages share the tool output cap.</summary>
    public const int StartingHits = 10;
    /// <summary>The call id of the starting search in the history the model reads.</summary>
    public const string StartingCallId = "atlas-start";
    public static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(120);

    public async Task<AiMessageView> RunAsync(Guid messageId, AnswerPromptInput input, AiProfileSnapshot profile,
        Func<AgentStepEvent, CancellationToken, Task>? onStep, CancellationToken ct)
    {
        var context = http.HttpContext ?? throw new InvalidOperationException("An agent turn runs in a request.");
        var scope = input.Scope ?? AiMessageScope.Wiki;
        IReadOnlySet<string>? off = await capabilities.IsAvailableAsync(AiTaskCatalog.AssistantArrange, ct) ? null : new HashSet<string> { AssistantToolGroups.MenuGroup };
        var selection = AssistantToolGroups.Select(input.Question, input.EarlierQuestions,
            new AssistantToolContext(scope.Kind == AiScopeKind.Page, scope.Kind == AiScopeKind.Space,
                context.RequestServices.GetRequiredService<ModuleCatalog>().IsEnabled("projector")), off);
        using var timeout = new CancellationTokenSource(TurnTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var turn = new Turn(onStep);
        var started = clock.GetTimestamp();
        ChatResponse response;
        IReadOnlyList<AssistantReturnedPage> returned;
        AiProposalDraft? proposal;
        try
        {
            await using var tools = await AssistantToolSession.OpenAsync(context, selection, linked.Token);
            using var agent = new FunctionInvokingChatClient(factory.Chat(profile, AiTaskCatalog.AssistantAgent))
            {
                MaximumIterationsPerRequest = MaxToolRounds,
            };
            var messages = Messages(input, scope);
            if (!selection.Groups.Any(AssistantToolGroups.WriteGroups.Contains))
            {
                await StartingSearchAsync(tools, turn, messages, input.Question, scope, linked.Token);
            }
            response = await agent.GetResponseAsync(messages,
                new ChatOptions { Tools = [.. tools.Tools.OfType<AIFunction>().Select(tool => new StepFunction(tool, tools.Results, turn))], MaxOutputTokens = MaxOutputTokens },
                linked.Token);
            if (string.IsNullOrWhiteSpace(response.Text) && tools.Proposal is null)
            {
                // The loop ended on tool calls (iteration limit) or on nothing: one more call, without tools, that must answer from what was found.
                response = await FinalAnswerAsync(factory.Chat(profile, AiTaskCatalog.AssistantAgent), messages, response, linked.Token);
            }
            returned = tools.Results.Pages;
            proposal = tools.Proposal;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await AbandonAsync(messageId, AiMessageState.Canceled, null);
            throw;
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            // Spec §4.4: the turn has 120 s; what the loop had is not an answer.
            await AbandonAsync(messageId, AiMessageState.Failed, AiOutcome.Timeout);
            throw AiAnswerProblems.CouldNotPrepare();
        }
        catch (AiCallException failure) when (AiOutcome.IsAvailabilityFailure(failure.Code))
        {
            await AbandonAsync(messageId, AiMessageState.Unavailable, failure.Code);
            throw AiGate.Unavailable();
        }
        catch (AiCallException failure)
        {
            await AbandonAsync(messageId, AiMessageState.Failed, failure.Code);
            throw AiAnswerProblems.CouldNotPrepare();
        }
        var latency = (int)Math.Clamp(Math.Round(clock.GetElapsedTime(started).TotalMilliseconds), 0, int.MaxValue);
        // A proposal must not be lost with an empty answer: the card needs a message to stand under.
        var text = proposal is not null && string.IsNullOrWhiteSpace(response.Text) ? AssistantProposals.FallbackAnswer : response.Text;
        if (AgentAnswer.Build(text, returned) is not { } answer)
        {
            await AbandonAsync(messageId, AiMessageState.Failed, AiOutcome.InvalidOutput);
            throw AiAnswerProblems.CouldNotPrepare();
        }
        var model = string.IsNullOrWhiteSpace(response.ModelId) ? profile.Model : response.ModelId;
        var metrics = new AiAnswerMetrics(AgentPrompt.Version, profile.RevisionId, model.Length > 200 ? model[..200] : model,
            ToInt(response.Usage?.InputTokenCount), ToInt(response.Usage?.OutputTokenCount), latency);
        try
        {
            var view = await bus.InvokeAsync<AiMessageView>(new ReleaseAgentAnswer(messageId, answer, turn.Steps, metrics, proposal), ct);
            if (view.ProposalId is { } proposalId)
            {
                await proposals.AuditProposalAsync(await proposals.GetAsync(proposalId, CancellationToken.None));
            }
            return view;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await AbandonAsync(messageId, AiMessageState.Canceled, null);
            throw;
        }
    }

    /// <summary>The system prompt, the history window as earlier exchanges, then the question with the panel's open page or space by id.</summary>
    private static List<ChatMessage> Messages(AnswerPromptInput input, AiMessageScope scope)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, AgentPrompt.System) };
        foreach (var turn in input.History)
        {
            messages.Add(new ChatMessage(ChatRole.User, turn.Question));
            messages.Add(new ChatMessage(ChatRole.Assistant, string.Join(' ', turn.AnswerSentences) + (turn.Outcome is { } outcome ? "\n" + outcome : "")));
        }
        var open = scope switch
        {
            { Kind: AiScopeKind.Page, PageId: { } page } => $"\n\n(V panelu je otevřená stránka, pageId: {page}.)",
            { Kind: AiScopeKind.Space, SpaceId: { } space } => $"\n\n(V panelu je otevřený prostor, spaceId: {space}.)",
            _ => "",
        };
        messages.Add(new ChatMessage(ChatRole.User, input.Question + open));
        return messages;
    }

    /// <summary>
    /// A question's first search, run before the first model call (prompt <c>agent@5</c>): <c>atlas_search</c> with the question through the
    /// turn's own session (the user's rights, G5, the sources, the output cap and a step like any call), its result in the history as the
    /// model's own call, so a question the passages answer needs one model call instead of two or more. A failed search (rate limit, a tool
    /// not offered) leaves the history as it was: the model searches itself.
    /// </summary>
    private static async Task StartingSearchAsync(AssistantToolSession tools, Turn turn, List<ChatMessage> messages, string question,
        AiMessageScope scope, CancellationToken ct)
    {
        const string tool = "atlas_search";
        var arguments = new Dictionary<string, object?>
        {
            ["query"] = Sources.AnswerQueryText.Cut(question, Search.Domain.SemanticQueryService.MaxQueryCharacters), ["limit"] = StartingHits,
        };
        if (scope is { Kind: AiScopeKind.Space, SpaceId: { } space })
        {
            arguments["spaceId"] = space.ToString();
        }
        var id = turn.NextId();
        await turn.ReportAsync(new AgentStepEvent(id, "start", tool, null, null), ct);
        var result = await tools.CallAsync(tool, arguments, ct);
        await turn.ReportAsync(new AgentStepEvent(id, "end", tool, null, result.IsError != true), ct);
        if (result.IsError == true)
        {
            return;
        }
        messages.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(StartingCallId, tool, arguments)]));
        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(StartingCallId, JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions))]));
    }

    /// <summary>The last call of a turn that produced no text: the history with the tool results (a call nobody answered is dropped), no tools, a nudge to answer.</summary>
    private static Task<ChatResponse> FinalAnswerAsync(IChatClient client, List<ChatMessage> messages, ChatResponse response, CancellationToken ct)
    {
        var answered = response.Messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).Select(result => result.CallId).ToHashSet();
        var history = new List<ChatMessage>(messages);
        foreach (var message in response.Messages)
        {
            var contents = message.Contents.Where(content => content is not FunctionCallContent call || answered.Contains(call.CallId)).ToList();
            if (contents.Count > 0)
            {
                history.Add(new ChatMessage(message.Role, contents));
            }
        }
        history.Add(new ChatMessage(ChatRole.User, "Další vyhledávání už není možné. Odpověz teď česky z toho, co jsi našel, a řekni, co chybí nebo co se nepodařilo zjistit."));
        return client.GetResponseAsync(history, new ChatOptions { MaxOutputTokens = MaxOutputTokens }, ct);
    }

    /// <summary>Always completes, even after the request was aborted: the answer must not stay "preparing".</summary>
    private Task AbandonAsync(Guid messageId, string state, string? errorCode)
        => bus.InvokeAsync(new AbandonAiTurn(messageId, state, errorCode), CancellationToken.None);

    private static int? ToInt(long? value) => value is null ? null : (int)Math.Clamp(value.Value, 0, int.MaxValue);

    /// <summary>The steps of one turn: numbered in call order, kept as tool + ok for the stored message.</summary>
    private sealed class Turn(Func<AgentStepEvent, CancellationToken, Task>? onStep)
    {
        private readonly List<AiAgentStep> m_steps = [];
        private int m_next;

        public IReadOnlyList<AiAgentStep> Steps
        {
            get { lock (m_steps) return [.. m_steps]; }
        }

        public string NextId() => "s" + Interlocked.Increment(ref m_next);

        public async Task ReportAsync(AgentStepEvent step, CancellationToken ct)
        {
            if (step.Ok is { } ok)
            {
                lock (m_steps) m_steps.Add(new AiAgentStep(step.Tool, ok));
            }
            if (onStep is not null)
            {
                await onStep(step, ct);
            }
        }
    }

    /// <summary>The function-invocation hook: a tool of the session that reports its start and end, never its arguments or output.</summary>
    private sealed class StepFunction(AIFunction tool, AssistantToolResults results, Turn turn) : DelegatingAIFunction(tool)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var id = turn.NextId();
            await turn.ReportAsync(new AgentStepEvent(id, "start", Name, results.Target(arguments), null), cancellationToken);
            object? result;
            try
            {
                result = await base.InvokeCoreAsync(arguments, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await turn.ReportAsync(new AgentStepEvent(id, "end", Name, results.Target(arguments), false), cancellationToken);
                throw;
            }
            // An MCP error result comes back as the CallToolResult JSON with isError (McpClientTool).
            var failed = result is JsonElement { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("isError", out var isError)
                && isError.ValueKind == JsonValueKind.True;
            await turn.ReportAsync(new AgentStepEvent(id, "end", Name, results.Target(arguments), !failed), cancellationToken);
            return result;
        }
    }
}
