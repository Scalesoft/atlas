using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Assistant.Agent;
using Atlas.Server.Features.Assistant.Answering;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Persistence;
using Atlas.Server.Features.Assistant.Queries;
using Atlas.Server.Features.Assistant.Sources;
using Atlas.Server.Features.SearchGaps;

namespace Atlas.Server.Features.Assistant.Commands;

/// <summary>
/// The check before the model (R2 steps 2–4, plan phase 3 §7 step 2): the owner's answer must still be preparing and younger
/// than 10 minutes, the current chat revision compatible with the conversation (R5), every dependency authorized again
/// (<see cref="AnswerTurnGate"/>) and every source text still current. Otherwise the answer ends here and the model is never
/// called: <c>withheld</c> after a revocation, <c>failed</c> with <c>sources_changed</c> after a newer publication or a new
/// index generation, <c>failed</c> with <c>conversation_outdated</c> after an incompatible profile change, <c>canceled</c> when
/// it waited too long.
/// </summary>
public sealed class BeginAiCompletionHandler(ICurrentActor actor, ConversationStore store, SourceRegistry registry, AnswerTurnGate gate,
    TimeProvider clock, AiProposalStore proposals)
{
    public async Task<AiCompletionStart> Handle(BeginAiCompletion command, CancellationToken ct)
    {
        var owned = await AnswerTurns.PreparingAsync(store, actor, command.MessageId, ct);
        var message = owned.Message;
        var now = clock.GetUtcNow().UtcDateTime;
        if (now - message.CreatedAt > AiConversationRetention.PreparingTimeout)
        {
            return Finished(await AnswerTurns.EndAsync(store, message, AiMessageState.Canceled, null, now, ct));
        }
        if (!string.Equals(owned.Conversation.ChatCompatibility, command.ChatCompatibility, StringComparison.Ordinal))
        {
            return Finished(await AnswerTurns.EndAsync(store, message, AiMessageState.Failed, AiAnswerProblems.ConversationOutdated, now, ct));
        }
        var agent = message.Task == AiTaskCatalog.AssistantAgent;
        var window = AnswerHistoryWindow.Select(await store.HistoryAsync(message.ConversationId, AnswerPromptBuilder.MaxHistoryTurns, ct));
        // An agent turn stores no dependency before the model: the history its prompt repeats is checked here and stored at release.
        var dependencies = agent ? AnswerTurns.HistoryRows(window, []) : await AnswerTurns.DependenciesAsync(store, message.Id, ct);
        var decision = await gate.DecideAsync(dependencies, ct);
        if (decision.Outcome != AnswerGateOutcome.Allowed)
        {
            return Finished(await AnswerTurns.EndAsync(store, message, AnswerTurns.StateOf(decision), AnswerTurns.ErrorOf(decision), now, ct));
        }
        if (agent)
        {
            var earlier = (await store.MessagesAsync(message.ConversationId, owned.Conversation.OwnerId, ct))
                .Where(row => row.Role == AiMessageRole.User && row.Ordinal < message.Ordinal).ToArray();
            // What became of earlier proposals goes with their answers: "a teď stejně pro Personální" needs what was applied.
            var outcomes = (await proposals.ForMessagesAsync(window.Select(turn => turn.Answer.Id).ToArray(), ct))
                .ToDictionary(row => row.MessageId, row => AssistantProposals.Outcome(row, now));
            var history = AnswerHistoryWindow.Prompt(window)
                .Select((turn, index) => turn with { Outcome = outcomes.GetValueOrDefault(window[index].Answer.Id) }).ToArray();
            return new AiCompletionStart(null, new AnswerPromptInput(earlier[^1].Question ?? "", [], history,
                earlier[^1].Scope, earlier[..^1].Select(row => row.Question ?? "").ToArray()));
        }
        var context = dependencies.Where(row => row.Kind == AiDependencyKind.Context).OrderBy(row => row.Ordinal).ToArray();
        var references = context.Select(row => decision.References[row.Ordinal]).ToArray();
        var texts = await registry.LoadTextAsync(references, ct);
        var descriptions = await registry.DescribeAsync(references, ct);
        if (references.Any(reference => texts.GetValueOrDefault(reference) is null || descriptions.GetValueOrDefault(reference) is null))
        {
            // The chunk text is gone (a new publication reindexed, a new generation was activated): the exact text the
            // dependency names cannot be sent, and another one must not be (R2 step 5).
            return Finished(await AnswerTurns.EndAsync(store, message, AiMessageState.Failed, AiAnswerProblems.SourcesChanged, now, ct));
        }
        var messages = await store.MessagesAsync(message.ConversationId, owned.Conversation.OwnerId, ct);
        var question = messages.Single(row => row.Ordinal == message.Ordinal - 1).Question ?? "";
        return new AiCompletionStart(null, new AnswerPromptInput(question,
            references.Select(reference => new AnswerPromptSource(descriptions[reference]!.Title, descriptions[reference]!.HeadingTitle, texts[reference]!)).ToArray(),
            AnswerHistoryWindow.Prompt(window)));
    }

    private static AiCompletionStart Finished(AiMessageView view) => new(view, null);
}

/// <summary>
/// The revocation gate (R2): the release of an answer is the commit of this transaction. The model's answer is written only when
/// every dependency still passes (<see cref="AnswerTurnGate"/>): otherwise <c>withheld</c> without its text (spec RAG §5: the
/// whole pending answer is discarded) or <c>failed</c> with <c>sources_changed</c> (a source was published again: the old
/// revision may hold text its author removed). A model that found no answer ends <c>no_sources</c> and is a knowledge gap (G2).
/// The operating metadata is kept in every case, never content.
/// </summary>
public sealed class ReleaseAiAnswerHandler(ICurrentActor actor, ConversationStore store, ConversationReader reader, AnswerTurnGate gate,
    ISearchMissRecorder misses, TimeProvider clock)
{
    public async Task<AiMessageView> Handle(ReleaseAiAnswer command, CancellationToken ct)
    {
        var owned = await AnswerTurns.PreparingAsync(store, actor, command.MessageId, ct);
        var message = owned.Message;
        var now = clock.GetUtcNow().UtcDateTime;
        var metrics = command.Metrics;
        var answer = command.Answer;
        var dependencies = await AnswerTurns.DependenciesAsync(store, message.Id, ct);
        var decision = await gate.DecideAsync(dependencies, ct);
        if (decision.Outcome != AnswerGateOutcome.Allowed)
        {
            var state = AnswerTurns.StateOf(decision);
            await Complete(state, null, AnswerTurns.ErrorOf(decision), []);
            return AnswerViews.Answer(message, state, AnswerTurns.ErrorOf(decision), null, [], null);
        }
        AiStoredAnswer? stored = null;
        if (answer.NoSources)
        {
            await Complete(AiMessageState.NoSources, null, null, []);
            var question = (await store.MessagesAsync(message.ConversationId, owned.Conversation.OwnerId, ct)).Single(row => row.Ordinal == message.Ordinal - 1);
            if (message.Task != Atlas.Server.Features.Ai.Settings.AiTaskCatalog.PageSummary)
                PrepareAiTurnHandler.Record(owned.Conversation.OwnerId, question.Scope ?? AiMessageScope.Wiki, question.Question ?? "", misses);
        }
        else
        {
            stored = new AiStoredAnswer(true, answer.Sentences.Select(sentence => new AiStoredSentence(sentence.Text, sentence.Sources)).ToArray());
            await Complete(AiMessageState.Answered, stored, null, answer.Cited);
        }
        var messages = await store.MessagesAsync(message.ConversationId, owned.Conversation.OwnerId, ct);
        var view = await reader.MessagesAsync(messages.Where(row => row.Id == message.Id).ToArray(), ct);
        return view.Messages.Single();

        async Task Complete(string state, AiStoredAnswer? text, string? error, IReadOnlyCollection<int> cited)
        {
            if (!await store.CompleteAsync(message.Id, new AiAnswerCompletion(state, text, error, now, cited, metrics.PromptVersion,
                    metrics.ProfileRevisionId, metrics.Model, metrics.InputTokens, metrics.OutputTokens, metrics.LatencyMs, answer.InvalidCitations,
                    answer.DroppedSentences), ct))
            {
                throw AiAnswerProblems.Finished();
            }
        }
    }
}

/// <summary>
/// The revocation gate of an agent turn (WP-14), the commit of this transaction like <see cref="ReleaseAiAnswerHandler"/>. Each source
/// names the page's current published revision (a page no longer published was lost during the turn: withheld). Sources are only pages
/// read or found published (<see cref="AssistantToolResults"/>): a draft read in its working version is neither a source nor a dependency
/// and never withholds the answer (the reader is its editor; working text reached the model only under spec §7.1). With the history the
/// prompt repeated, every dependency must still pass <see cref="AnswerTurnGate"/>, otherwise the answer is withheld without its
/// text and steps. Then the dependencies, the answer, the cited sources, the steps and the turn's write proposal (pending for
/// <see cref="AiToolProposalRow.Lifetime"/>, spec §6.1) are stored. A withheld answer drops its proposal. Metadata is kept in every case.
/// </summary>
public sealed class ReleaseAgentAnswerHandler(ICurrentActor actor, ConversationStore store, ConversationReader reader, AnswerTurnGate gate,
    AnswerSourceSql sources, TimeProvider clock, AiProposalStore proposals)
{
    public async Task<AiMessageView> Handle(ReleaseAgentAnswer command, CancellationToken ct)
    {
        var owned = await AnswerTurns.PreparingAsync(store, actor, command.MessageId, ct);
        var message = owned.Message;
        var now = clock.GetUtcNow().UtcDateTime;
        var pages = command.Answer.Sources;
        var revisions = await sources.PublishedRevisionsAsync(pages.Select(page => page.PageId).ToArray(), ct);
        var context = pages.Where(page => revisions.ContainsKey(page.PageId))
            .Select((page, index) => AiSourceDependencyRow.Page(index + 1, AiDependencyKind.Context, page.PageId, revisions[page.PageId])).ToArray();
        var window = AnswerHistoryWindow.Select(await store.HistoryAsync(message.ConversationId, AnswerPromptBuilder.MaxHistoryTurns, ct));
        AiSourceDependencyRow[] dependencies = [.. context, .. AnswerTurns.HistoryRows(window, context)];
        var decision = await gate.DecideAsync(dependencies, ct);
        if (context.Length < pages.Count || decision.Outcome != AnswerGateOutcome.Allowed)
        {
            var state = context.Length < pages.Count ? AiMessageState.Withheld : AnswerTurns.StateOf(decision);
            var error = context.Length < pages.Count ? null : AnswerTurns.ErrorOf(decision);
            await Complete(state, null, error, [], null);
            return AnswerViews.Answer(message, state, error, null, [], null);
        }
        await store.AddDependenciesAsync(message.Id, dependencies, ct);
        await Complete(AiMessageState.Answered, command.Answer.Answer, null, command.Answer.Cited, command.Steps);
        if (command.Proposal is { } proposal)
        {
            // One open proposal per conversation: the preparation already closed the earlier one; this keeps the rule absolute.
            await proposals.SupersedeAsync(message.ConversationId, now, ct);
            await proposals.InsertAsync(new AiToolProposalRow(Guid.NewGuid(), message.Id, message.ConversationId, proposal.Tool, proposal.Arguments,
                proposal.Items, AiProposalState.Pending, null, now, now + AiToolProposalRow.Lifetime, null), ct);
        }
        var messages = await store.MessagesAsync(message.ConversationId, owned.Conversation.OwnerId, ct);
        return (await reader.MessagesAsync(messages.Where(row => row.Id == message.Id).ToArray(), ct)).Messages.Single();

        async Task Complete(string state, AiStoredAnswer? answer, string? error, IReadOnlyCollection<int> cited, IReadOnlyList<AiAgentStep>? steps)
        {
            var metrics = command.Metrics;
            if (!await store.CompleteAsync(message.Id, new AiAnswerCompletion(state, answer, error, now, cited, metrics.PromptVersion,
                    metrics.ProfileRevisionId, metrics.Model, metrics.InputTokens, metrics.OutputTokens, metrics.LatencyMs, Steps: steps), ct))
            {
                throw AiAnswerProblems.Finished();
            }
        }
    }
}

/// <summary>Ends a preparing answer of the owner without a release (canceled, unavailable, failed); a no-op once it ended.</summary>
public sealed class AbandonAiTurnHandler(ICurrentActor actor, ConversationStore store, TimeProvider clock)
{
    public async Task Handle(AbandonAiTurn command, CancellationToken ct)
    {
        var owned = await store.MessageAsync(command.MessageId, actor.Claimed().Id, forUpdate: true, ct);
        if (owned is { Message: { Role: AiMessageRole.Assistant, State: AiMessageState.Preparing } message })
        {
            await AnswerTurns.EndAsync(store, message, command.State, command.ErrorCode, clock.GetUtcNow().UtcDateTime, ct);
        }
    }
}

/// <summary>The steps the handlers of one answer share.</summary>
internal static class AnswerTurns
{
    /// <summary>The owner's answer locked FOR UPDATE: 404 when it is not the owner's answer (never 403), 409 turn_finished
    /// when it no longer prepares. The owner comes from the session's claims; the gate checks the account itself under lock.</summary>
    public static async Task<AiOwnedMessage> PreparingAsync(ConversationStore store, ICurrentActor actor, Guid messageId, CancellationToken ct)
    {
        var owned = await store.MessageAsync(messageId, actor.Claimed().Id, forUpdate: true, ct);
        if (owned is null || owned.Message.Role != AiMessageRole.Assistant)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "Zpráva nebyla nalezena.");
        }
        if (owned.Message.State != AiMessageState.Preparing)
        {
            throw AiAnswerProblems.Finished();
        }
        return owned;
    }

    public static async Task<IReadOnlyList<AiSourceDependencyRow>> DependenciesAsync(ConversationStore store, Guid messageId, CancellationToken ct)
        => (await store.DependenciesAsync([messageId], ct)).GetValueOrDefault(messageId) ?? [];

    /// <summary>The dependencies of the history window as <c>history</c> rows after <paramref name="context"/>, without a source that
    /// is already among them (the transitive closure of R4, as the preparation copies it).</summary>
    public static IReadOnlyList<AiSourceDependencyRow> HistoryRows(IReadOnlyList<AiHistoryTurn> window, IReadOnlyList<AiSourceDependencyRow> context)
    {
        var identities = context.Select(row => row.Identity).ToHashSet();
        return window.SelectMany(turn => turn.Dependencies).Where(row => identities.Add(row.Identity))
            .Select((row, index) => row.AsHistory(context.Count + index + 1)).ToArray();
    }

    /// <summary>Ends the answer in <paramref name="state"/> without an answer, keeping what the preparation stored.</summary>
    public static async Task<AiMessageView> EndAsync(ConversationStore store, AiMessageRow message, string state, string? errorCode, DateTime now,
        CancellationToken ct)
    {
        if (!await store.CompleteAsync(message.Id, new AiAnswerCompletion(state, null, errorCode, now, []), ct))
        {
            throw AiAnswerProblems.Finished();
        }
        return AnswerViews.Answer(message, state, errorCode, null, [], null);
    }

    public static string StateOf(AnswerGateDecision decision)
        => decision.Outcome == AnswerGateOutcome.Withheld ? AiMessageState.Withheld : AiMessageState.Failed;

    public static string? ErrorOf(AnswerGateDecision decision)
        => decision.Outcome == AnswerGateOutcome.Changed ? AiAnswerProblems.SourcesChanged : null;
}
