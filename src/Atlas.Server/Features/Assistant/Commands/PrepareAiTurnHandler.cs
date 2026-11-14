using Atlas.Server.Features.Assistant.Answering;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Persistence;
using Atlas.Server.Features.Assistant.Prompts;
using Atlas.Server.Features.Assistant.Sources;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Features.SearchGaps;
using Atlas.Server.Features.SearchGaps.Contracts;

namespace Atlas.Server.Features.Assistant.Commands;

/// <summary>
/// Plan phase 3 §7 <c>PrepareAiTurnHandler</c>, one short transaction: the actor locked with the scope's space (the readers'
/// protocol, a revoked credential is 401), for a follow-up the owner's conversation FOR UPDATE, the daily limit, the scope
/// (a space the actor reads, a published page the actor reads, else 404 as everywhere), retrieval through the adapters (ACL inside
/// their SQL, published revisions of spaces included in AI only), then the stored turn. An answer without sources is complete at
/// once (<c>no_sources</c>) and recorded as a knowledge gap (G2, R9). Logs nothing.
/// </summary>
public sealed class PrepareAiTurnHandler(IContentAccess access, IAtlasLocks locks, ConversationStore store, SourceRegistry registry,
    AnswerSourceSql sources, ISearchMissRecorder misses, TimeProvider clock, Atlas.Server.Features.Ai.Access.AiPolicyResolver policy,
    AiProposalStore proposals)
{
    public async Task<AiTurnPrepared> Handle(PrepareAiTurn command, CancellationToken ct)
    {
        var (actor, scope, stored) = await ResolveScopeAsync(command.Scope, ct);
        var permission = await RequirePermissionAsync(actor.Id, command.Task, ct);
        AiConversationRow? conversation = null;
        IReadOnlyList<AiSourceDependencyRow> history = [];
        if (command.ConversationId is { } conversationId)
        {
            conversation = await store.LockOwnedAsync(conversationId, actor.Id, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "Konverzace nebyla nalezena.");
            if (await store.HasPreparingAsync(conversation.Id, ct))
            {
                throw AiAnswerProblems.InProgress();
            }
            // R5: only on a compatible revision; a key rotation or a new timeout silently moves the conversation to it.
            if (!string.Equals(conversation.ChatCompatibility, command.ChatCompatibility, StringComparison.Ordinal))
            {
                throw AiAnswerProblems.Outdated();
            }
            if (conversation.ChatProfileRevisionId != command.ProfileRevisionId)
            {
                await store.RepinProfileAsync(conversation.Id, command.ProfileRevisionId, command.ChatCompatibility, ct);
            }
            await RequireAvailableAsync(conversation, actor.Id, ct);
            history = HistoryDependencies(AnswerHistoryWindow.Select(await store.HistoryAsync(conversation.Id, AnswerPromptBuilder.MaxHistoryTurns, ct)));
        }

        var now = await ReserveQuotaAsync(actor.Id, command.Task, permission.Limit(command.Task), ct);
        if (command.ConversationId is not null)
        {
            // Spec §6.1 step 5, S7: a new message closes the open proposal; the model proposes again if asked.
            await proposals.SupersedeAsync(conversation!.Id, now, ct);
        }
        conversation ??= new AiConversationRow(Guid.NewGuid(), actor.Id, command.Origin, command.Origin == AiConversationOrigin.Assistant,
            command.ProfileRevisionId, command.ChatCompatibility, now, now, 0);

        var summary = command.Task == AiTaskCatalog.PageSummary;
        // An agent turn retrieves nothing here: its tools read during the completion, its sources and history are stored at release.
        var agent = command.Task == AiTaskCatalog.AssistantAgent;
        var all = summary ? await sources.SummarySourcesAsync(scope.PageId!.Value, ct) : [];
        var found = summary
            ? new AnswerSourceSet(SummaryPrompt.Select(all), AnswerRetrieval.Lexical)
            : agent ? new AnswerSourceSet([], AnswerRetrieval.Lexical)
            : await RetrieveAsync(actor, command, scope, ct);
        var truncated = summary && found.Sources.Count < all.Count;
        if (command.ConversationId is null)
        {
            await store.InsertConversationAsync(conversation, ct);
        }
        var ordinal = await store.NextOrdinalAsync(conversation.Id, ct);
        var state = found.Sources.Count == 0 && !agent ? AiMessageState.NoSources : AiMessageState.Preparing;
        var question = AiMessageRow.UserQuestion(Guid.NewGuid(), conversation.Id, ordinal, command.Question, stored, now);
        var answer = AiMessageRow.PreparedAnswer(Guid.NewGuid(), conversation.Id, ordinal + 1, state, command.Task, agent ? null : found.Retrieval,
            summary ? SummaryPrompt.Version : agent ? AgentPrompt.Version : AnswerPrompt.Version, command.ProfileRevisionId, command.Model, now)
            with { SourceCoverageTruncated = truncated };
        var context = found.Sources.Select((source, index) => AnswerSourceColumns.Row(index + 1, AiDependencyKind.Context, registry.Columns(source))).ToArray();
        var identities = context.Select(row => row.Identity).ToHashSet();
        var copied = history.Where(row => identities.Add(row.Identity)).Select((row, index) => row.AsHistory(context.Length + index + 1));
        // A turn without sources has nothing to answer from, and therefore no dependency either.
        await store.InsertTurnAsync(question, answer, state == AiMessageState.NoSources || agent ? [] : [.. context, .. copied], ct);
        if (state == AiMessageState.NoSources && command.Task != AiTaskCatalog.PageSummary)
        {
            Record(actor.Id, stored, command.Question, misses);
        }
        return new AiTurnPrepared(conversation.Id, answer.Id, state, found.Sources.Select((source, index) => new AiSourceView(index + 1,
            source.Reference.Type, source.Chunk?.PageId, source.SpaceId, source.SpaceName, source.Title, source.HeadingTitle, source.HeadingId,
            AiSourceView.ExcerptOf(source.Text), false, false)).ToArray(), truncated);
    }

    /// <summary>Today's retrieval, or with the reranker's order (<see cref="PrepareAiTurn.Preferred"/>) the same pool read again under this
    /// transaction's locks: its sources in that order (one new since the order comes after the ordered ones, one gone is gone), cut to the
    /// effort's sources.</summary>
    private async Task<AnswerSourceSet> RetrieveAsync(UserAccount actor, PrepareAiTurn command, AnswerScope scope, CancellationToken ct)
    {
        var effort = command.Effort ?? AnswerEffort.Quick;
        if (command.Preferred is not { } preferred)
        {
            return await registry.RetrieveAsync(AnswerRetrievalRequest.For(actor, command.Question, scope, command.Vector, effort), ct);
        }
        var pool = await registry.RetrieveAsync(AnswerRetrievalRequest.Pool(actor, command.Question, scope, command.Vector, effort), ct);
        var rank = preferred.Select((reference, index) => (reference, index)).DistinctBy(x => x.reference).ToDictionary(x => x.reference, x => x.index);
        return pool with
        {
            Sources = pool.Sources.Select((source, index) => (source, index))
                .OrderBy(x => rank.GetValueOrDefault(x.source.Reference, int.MaxValue)).ThenBy(x => x.index)
                .Take(effort.Take).Select(x => x.source).ToArray(),
        };
    }

    private async Task<Atlas.Server.Features.Ai.Access.AiResolvedPolicy> RequirePermissionAsync(Guid actorId, string task, CancellationToken ct)
    {
        var permission = await policy.ResolveAsync(actorId, ct);
        if (!permission.Enabled) throw Atlas.Server.Features.Ai.Capabilities.AiGate.Unavailable();
        Atlas.Server.Features.Ai.Capabilities.AiGate.RequireTools(permission, task);
        return permission;
    }

    private async Task<DateTime> ReserveQuotaAsync(Guid actorId, string task, int? dailyLimit, CancellationToken ct)
    {
        if (dailyLimit is not { } limit) return clock.GetUtcNow().UtcDateTime;
        // The content/actor lock is held by preparation; count and reservation share its transaction.
        await locks.AcquireAdvisoryAsync($"ai-limit:{actorId:N}:{task}", ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var (from, to) = Atlas.Server.Features.Ai.Access.AiQuotaDay.Bounds(now);
        if (await store.DailyCountAsync(actorId, task, from, to, ct) >= limit)
            throw AiAnswerProblems.LimitReached();
        return now;
    }

    /// <summary>Records a safe knowledge gap under the existing search-miss policy.</summary>
    internal static void Record(Guid actorId, AiMessageScope scope, string question, ISearchMissRecorder misses)
    {
        var display = SearchMissPolicy.Display(question);
        if (display.Length > SearchMissPolicy.MaximumLength)
        {
            return;
        }
        misses.Observe(new SearchObservation(actorId, scope.SpaceId, question, SearchText.Normalize(display), false, SearchGapReason.NoAnswer));
    }

    /// <summary>The scope only narrows (R3). A space the actor does not read and a page that is not a published page the actor
    /// reads answer 404 with the messages of the rest of the wiki, never telling which.</summary>
    private async Task<(UserAccount Actor, AnswerScope Scope, AiMessageScope Stored)> ResolveScopeAsync(AiScope scope, CancellationToken ct)
    {
        switch (scope.Kind)
        {
            case AiScopeKind.Space:
            {
                var spaceId = scope.SpaceId!.Value;
                var actor = await access.LockActorAsync(spaceId, ct);
                await access.RequireSpaceAsync(spaceId, SpaceRole.Reader, ct);
                return (actor, AnswerScope.ForSpace(spaceId), new AiMessageScope(AiScopeKind.Space, spaceId, null));
            }
            case AiScopeKind.Page:
            {
                var pageId = scope.PageId!.Value;
                var spaces = await sources.CurrentSpacesAsync([pageId], ct);
                var actor = await access.LockActorAsync(spaces.TryGetValue(pageId, out var spaceId) ? spaceId : (Guid?)null, ct);
                var page = spaces.ContainsKey(pageId) ? await locks.ReadPageSharedAsync(pageId, ct) : null;
                if (page is null || page.SpaceId != spaceId)
                {
                    throw PageNotFound();
                }
                (await access.AuthorizePageAsync(page, AccessOperation.ReadContent, ct)).Require();
                if (page.IsTrashed || page.IsTemplate || page.PublishedRevisionId is null)
                {
                    throw PageNotFound();
                }
                return (actor, AnswerScope.ForPage(pageId, page.SpaceId), new AiMessageScope(AiScopeKind.Page, page.SpaceId, pageId));
            }
            default:
                return (await access.LockActorAsync(null, ct), AnswerScope.Wiki, AiMessageScope.Wiki);
        }
    }

    private static ApiException PageNotFound() => new(StatusCodes.Status404NotFound, "Stránka nebyla nalezena.");

    /// <summary>A conversation with an unavailable dependency in any answer cannot go on (R4, spec RAG §4): 409
    /// conversation_unavailable, "začněte novou". A newer revision of a source is fine here (history needs access only).</summary>
    private async Task RequireAvailableAsync(AiConversationRow conversation, Guid ownerId, CancellationToken ct)
    {
        var answers = (await store.MessagesAsync(conversation.Id, ownerId, ct)).Where(message => message.Role == AiMessageRole.Assistant)
            .Select(message => message.Id).ToArray();
        var dependencies = (await store.DependenciesAsync(answers, ct)).Values.SelectMany(rows => rows).ToArray();
        if (dependencies.Length == 0)
        {
            return;
        }
        var references = dependencies.Select(row => registry.Reference(AnswerSourceColumns.Of(row))).ToArray();
        if (references.Any(reference => reference is null))
        {
            throw AiAnswerProblems.Unavailable();
        }
        var statuses = await registry.StatusAsync(references.Select(reference => reference!).Distinct().ToArray(), ct);
        if (statuses.Values.Any(status => status == SourceStatus.Unavailable))
        {
            throw AiAnswerProblems.Unavailable();
        }
    }

    /// <summary>Every dependency of the answers the prompt repeats (their context and their own history): the transitive closure
    /// without recursion (R4, spec RAG §5 bod 8).</summary>
    private static IReadOnlyList<AiSourceDependencyRow> HistoryDependencies(IReadOnlyList<AiHistoryTurn> window)
        => window.SelectMany(turn => turn.Dependencies).ToArray();

}
