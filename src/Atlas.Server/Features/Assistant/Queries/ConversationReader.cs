using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Persistence;
using Atlas.Server.Features.Assistant.Sources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Assistant.Queries;

/// <summary>
/// Reading history with revalidation (plan phase 3 §8, R4, R5; spec RAG §4, §5 point 8, §9 Historie). Runs in the caller's
/// transaction after <c>IContentAccess.LockActorAsync</c>, for the owner only. Every source of every answer whose content is shown
/// (<c>answered</c>, and the sources of one still <c>preparing</c>) is shown only after every turn's dependencies are checked,
/// including terminal turns. A single unavailable source, of the answer's own prompt or copied from the history it repeats, withholds the answer with its question,
/// and the conversation loses its title in the list and cannot continue. A newer revision of a source is shown as changed, never
/// with its older heading or text. Never a title, count or text of a source the owner can no longer read. Nothing is logged.
/// </summary>
public sealed class ConversationReader(ConversationStore store, SourceRegistry sources, ISession session)
{
    /// <summary>Conversations per page of "Moje konverzace".</summary>
    public const int PageSize = 30;
    /// <summary>The title of a listed conversation: its first question, shortened.</summary>
    public const int TitleLength = 80;
    /// <summary>The excerpt of a source, from the indexed text the answer used.</summary>
    public const int ExcerptLength = AiSourceView.ExcerptLength;

    public static ApiException NotFound() => new(404, "Konverzace nebyla nalezena.");

    /// <summary>One page of the owner's listed conversations, revalidated in one batch of dependency status.</summary>
    public async Task<AiConversationList> ListAsync(UserAccount owner, DateTime? before, CancellationToken ct)
    {
        var items = await store.ListOwnedAsync(owner.Id, before, PageSize + 1, ct);
        var page = items.Take(PageSize).ToArray();
        var revalidated = await RevalidateAsync(page.SelectMany(item => item.AnswerIds).ToArray(), ct);
        var profiles = await ProfilesAsync(page.Select(item => item.Conversation.ChatProfileRevisionId), ct);
        var summaries = page.Select(item =>
        {
            var available = !item.AnswerIds.Any(revalidated.Withheld.Contains);
            return new AiConversationSummary(item.Conversation.Id, available ? Title(item.FirstQuestion) : null, available,
                item.Conversation.LastActivityAt, Provenance(profiles[item.Conversation.ChatProfileRevisionId], null));
        }).ToArray();
        return new AiConversationList(summaries, items.Count > PageSize ? page[^1].Conversation.LastActivityAt : null);
    }

    /// <summary>The owner's conversation, listed or not, revalidated; null when it is missing or somebody else's (404). Whether it
    /// may continue follows the current chat profile in <paramref name="configuration"/> (R5).</summary>
    public async Task<AiConversationView?> ViewAsync(UserAccount owner, Guid conversationId, AiConfigurationSnapshot configuration, CancellationToken ct)
    {
        var conversation = await store.GetOwnedAsync(conversationId, owner.Id, ct);
        if (conversation is null)
        {
            return null;
        }
        var (messages, available) = await MessagesAsync(await store.MessagesAsync(conversationId, owner.Id, ct), ct);
        var (canContinue, outdated) = Continuation(conversation, configuration, available);
        var profiles = await ProfilesAsync([conversation.ChatProfileRevisionId], ct);
        return new AiConversationView(conversation.Id, available, canContinue, outdated, Provenance(profiles[conversation.ChatProfileRevisionId], null),
            messages);
    }

    /// <summary>
    /// R5, O4: a conversation continues only on a compatible current chat profile (<see cref="ChatProfileCompatibility"/>), and only
    /// while AI and that profile are switched on and nothing of it is withheld. Outdated = a chat profile is configured but not
    /// compatible: read-only, "Začít novou". Without a chat profile it is neither.
    /// </summary>
    public static (bool CanContinue, bool Outdated) Continuation(AiConversationRow conversation, AiConfigurationSnapshot configuration, bool available)
    {
        if (configuration.Profile(AiProfileKind.Chat) is not { } chat)
        {
            return (false, false);
        }
        var compatible = ChatProfileCompatibility.IsCompatible(conversation.ChatCompatibility, chat);
        return (available && compatible && configuration.Enabled && chat.Enabled, !compatible);
    }

    /// <summary>
    /// The views of the messages of one conversation in order, revalidated, and whether none is withheld. Shared with the answer
    /// flow, whose completed answer is the same view: call it in a transaction after <c>LockActorAsync</c>.
    /// </summary>
    public async Task<(IReadOnlyList<AiMessageView> Messages, bool Available)> MessagesAsync(IReadOnlyList<AiMessageRow> messages, CancellationToken ct)
    {
        var shown = messages.Where(ShowsContent).ToArray();
        var revalidated = await RevalidateAsync(messages.Where(message => message.Role == AiMessageRole.Assistant).Select(message => message.Id).ToArray(), ct);
        var withheld = new HashSet<Guid>(revalidated.Withheld);

        // The context sources of the answers still shown, as the UI lists them ("Proč tato odpověď?": all of them, not only cited).
        var context = shown.Where(message => !withheld.Contains(message.Id))
            .SelectMany(message => revalidated.Sources(message.Id).Where(x => x.Dependency.Kind == AiDependencyKind.Context))
            .ToArray();
        var descriptions = await sources.DescribeAsync(context.Select(x => x.Reference!).Distinct().ToArray(), ct);
        var texts = await sources.LoadTextAsync(context.Where(x => x.Status == SourceStatus.Available).Select(x => x.Reference!).Distinct().ToArray(), ct);
        // A source that has nothing to show any more withholds its answer like an unavailable one.
        withheld.UnionWith(shown.Where(message => !withheld.Contains(message.Id) && revalidated.Sources(message.Id)
            .Any(x => x.Dependency.Kind == AiDependencyKind.Context && descriptions.GetValueOrDefault(x.Reference!) is null)).Select(message => message.Id));

        var questions = messages.Where(message => message.Role == AiMessageRole.Assistant && withheld.Contains(message.Id))
            .Select(message => message.Ordinal - 1).ToHashSet();
        var profiles = await ProfilesAsync(messages.Where(message => message.State == AiMessageState.Answered && message.ProfileRevisionId is not null)
            .Select(message => message.ProfileRevisionId!.Value), ct);
        var views = messages.Select(message => message.Role == AiMessageRole.User
            ? QuestionView(message, questions.Contains(message.Ordinal))
            : withheld.Contains(message.Id)
                ? Empty(message, AiMessageState.Withheld, null)
                : AnswerView(message, revalidated.Sources(message.Id), descriptions, texts, profiles)).ToArray();
        return (views, withheld.Count == 0);
    }

    private static bool ShowsContent(AiMessageRow message)
        => message.Role == AiMessageRole.Assistant && message.State is AiMessageState.Answered or AiMessageState.Preparing;

    private static AiMessageView QuestionView(AiMessageRow message, bool withheld)
        => new(message.Id, message.ConversationId, message.Role, null, withheld ? null : message.Question,
            withheld || message.Scope is not { } scope ? null : new AiScope(scope.Kind, scope.SpaceId, scope.PageId), [], [], null,
            message.CreatedAt, null, null);

    private static AiMessageView Empty(AiMessageRow message, string? state, string? errorCode)
        => new(message.Id, message.ConversationId, message.Role, state, null, null, [], [], null, message.CreatedAt, null, errorCode, message.SourceCoverageTruncated);

    private static AiMessageView AnswerView(AiMessageRow message, IReadOnlyList<RevalidatedSource> revalidated,
        IReadOnlyDictionary<SourceReference, SourceDescription?> descriptions, IReadOnlyDictionary<SourceReference, string?> texts,
        IReadOnlyDictionary<Guid, AiProfileRevision> profiles)
    {
        if (!ShowsContent(message))
        {
            // No text and no sources in any other state: a failed or revoked output is never presented as an answer (spec RAG §2).
            return Empty(message, message.State, message.ErrorCode);
        }
        var sourceViews = revalidated.Where(x => x.Dependency.Kind == AiDependencyKind.Context).Select(x =>
        {
            var description = descriptions[x.Reference!]!;
            var changed = x.Status == SourceStatus.Changed;
            return new AiSourceView(x.Dependency.Ordinal, x.Dependency.SourceType, description.PageId, description.SpaceId, description.SpaceName,
                description.Title, changed ? null : description.HeadingTitle, x.Dependency.HeadingId,
                changed ? null : AiSourceView.ExcerptOf(texts.GetValueOrDefault(x.Reference!)), x.Dependency.Cited, changed);
        }).ToArray();
        var answered = message.State == AiMessageState.Answered;
        var sentences = answered && message.Answer is { } answer
            ? answer.Sentences.Select(sentence => new AiSentenceView(sentence.Text, sentence.Sources)).ToArray()
            : [];
        var provenance = answered && message.ProfileRevisionId is { } revision && profiles.TryGetValue(revision, out var profile)
            ? Provenance(profile, message.Model)
            : null;
        return new AiMessageView(message.Id, message.ConversationId, message.Role, message.State, null, null, sentences, sourceViews, provenance,
            message.CreatedAt, null, message.ErrorCode, message.SourceCoverageTruncated, answered ? message.Steps : null,
            answered ? message.ProposalId : null);
    }

    /// <summary>The dependencies of these answers with their references and current status; an answer is withheld when any of its
    /// dependencies, context or history, no longer names a source an adapter serves or is unavailable to the actor.</summary>
    private async Task<Revalidated> RevalidateAsync(IReadOnlyCollection<Guid> answerIds, CancellationToken ct)
    {
        var dependencies = await store.DependenciesAsync(answerIds, ct);
        var located = dependencies.ToDictionary(pair => pair.Key, pair => pair.Value.Select(dependency => (dependency, reference: sources.Reference(
            new SourceColumns(dependency.SourceType, dependency.SourceVersion, dependency.PageId, dependency.RevisionId, dependency.ExternalKey,
                dependency.ExternalRevision, dependency.GenerationId, dependency.ChunkOrdinal, dependency.HeadingId)))).ToArray());
        var statuses = await sources.StatusAsync(located.Values.SelectMany(x => x).Where(x => x.reference is not null).Select(x => x.reference!)
            .Distinct().ToArray(), ct);
        var result = located.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<RevalidatedSource>)pair.Value.Select(x => new RevalidatedSource(
            x.dependency, x.reference, x.reference is null ? SourceStatus.Unavailable : statuses.GetValueOrDefault(x.reference, SourceStatus.Unavailable))).ToArray());
        var withheld = result.Where(pair => pair.Value.Any(x => x.Status == SourceStatus.Unavailable)).Select(pair => pair.Key).ToHashSet();
        return new Revalidated(result, withheld);
    }

    private async Task<IReadOnlyDictionary<Guid, AiProfileRevision>> ProfilesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return new Dictionary<Guid, AiProfileRevision>();
        }
        var rows = await session.QueryOver<AiProfileRevision>().WhereRestrictionOn(x => x.Id).IsIn(distinct).ListAsync(ct);
        return rows.ToDictionary(row => row.Id);
    }

    private static AiProvenanceView Provenance(AiProfileRevision profile, string? model)
        => AiProvenanceView.From(profile.Provider, profile.Location, model ?? profile.Model);

    /// <summary>The first question on one line, at most <see cref="TitleLength"/> characters.</summary>
    public static string? Title(string? question)
        => question is null ? null : Shorten(string.Join(' ', question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), TitleLength);

    private static string Shorten(string text, int length)
        => text.Length <= length ? text : text[..(length - 1)].TrimEnd() + "…";

    private sealed record RevalidatedSource(AiSourceDependencyRow Dependency, SourceReference? Reference, SourceStatus Status);

    private sealed record Revalidated(IReadOnlyDictionary<Guid, IReadOnlyList<RevalidatedSource>> ByMessage, IReadOnlySet<Guid> Withheld)
    {
        public IReadOnlyList<RevalidatedSource> Sources(Guid messageId) => ByMessage.GetValueOrDefault(messageId) ?? [];
    }
}
