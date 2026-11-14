using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlas.Server.Features.Assistant.Domain;

/// <summary>Where a conversation started (plan phase 3 R4): the answer card of the omnisearch or the assistant panel.</summary>
public static class AiConversationOrigin
{
    public const string Search = "search";
    public const string Assistant = "assistant";
    public const string Page = "page";

    public static bool IsKnown(string? origin) => origin is Search or Assistant or Page;
}

public static class AiMessageRole
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Page = "page";
}

/// <summary>The states of an answer (plan phase 3 §5). Only <see cref="Answered"/> carries an answer; a failed or revoked output is
/// never presented as one (spec RAG §2).</summary>
public static class AiMessageState
{
    public const string Preparing = "preparing";
    public const string Answered = "answered";
    public const string NoSources = "no_sources";
    public const string Withheld = "withheld";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
    public const string Unavailable = "unavailable";

    public static bool IsTerminal(string state) => state is Answered or NoSources or Withheld or Failed or Canceled or Unavailable;
}

/// <summary>The scope of a question; it only narrows (R3).</summary>
public static class AiScopeKind
{
    public const string Wiki = "wiki";
    public const string Space = "space";
    public const string Page = "page";

    public static bool IsKnown(string? kind) => kind is Wiki or Space or Page;
}

/// <summary><see cref="Context"/>: source [n] of the answer's own prompt. <see cref="History"/>: a dependency of an earlier answer
/// whose sentences the prompt repeats (R4, transitive closure without recursion).</summary>
public static class AiDependencyKind
{
    public const string Context = "context";
    public const string History = "history";
}

/// <summary>The one source type of 2.0 (AI-11): published pages, adapter version 1. Its dependencies keep the real FK to
/// <c>PageRevision</c>; any other type is stored by key and decided by its own adapter.</summary>
public static class AiPageSource
{
    public const string Type = "page";
    public const int Version = 1;
}

public static class AiConversationLimits
{
    /// <summary>A question longer than this is refused (spec RAG; CHECK in the migration).</summary>
    public const int QuestionMaxLength = 2000;
}

/// <summary>
/// Retention defaults of plan phase 3 (O3, Q4): an answer from the omnisearch that was never continued goes after 24 hours, a
/// listed conversation 180 days after its last activity, an answer left "preparing" (crashed client, restart) is canceled after
/// 10 minutes, and purging deletes at most 500 conversations per transaction. Record the values in the pilot's operating profile.
/// </summary>
public static class AiConversationRetention
{
    public static readonly TimeSpan Unlisted = TimeSpan.FromHours(24);
    public static readonly TimeSpan Listed = TimeSpan.FromDays(180);
    public static readonly TimeSpan PreparingTimeout = TimeSpan.FromMinutes(10);
    public const int PurgeBatch = 500;

    /// <summary>The cut-offs of one maintenance round at <paramref name="now"/>.</summary>
    public static (DateTime UnlistedBefore, DateTime ListedBefore, DateTime PreparedBefore) CutOffs(DateTime now)
        => (now - Unlisted, now - Listed, now - PreparingTimeout);
}

/// <summary>One row of <c>AiConversation</c>.</summary>
public sealed record AiConversationRow(Guid Id, Guid OwnerId, string Origin, bool Listed, Guid ChatProfileRevisionId,
    string ChatCompatibility, DateTime CreatedAt, DateTime LastActivityAt, int Version);

/// <summary>The scope a question was asked in.</summary>
public sealed record AiMessageScope(string Kind, Guid? SpaceId, Guid? PageId)
{
    public static readonly AiMessageScope Wiki = new(AiScopeKind.Wiki, null, null);
}

/// <summary>
/// One row of <c>AiMessage</c>: a question (<see cref="AiMessageRole.User"/>, <see cref="Question"/> and <see cref="Scope"/>) or an
/// answer (<see cref="AiMessageRole.Assistant"/>, state, task and operating metadata; <see cref="Answer"/> only when answered).
/// <see cref="ToString"/> never prints the question or the answer (logs carry no content, plan phase 3 §0.3).
/// </summary>
public sealed record AiMessageRow(
    Guid Id,
    Guid ConversationId,
    int Ordinal,
    string Role,
    string? State,
    string? Task,
    string? Question,
    AiMessageScope? Scope,
    AiStoredAnswer? Answer,
    string? PromptVersion,
    Guid? ProfileRevisionId,
    string? Model,
    int? InputTokens,
    int? OutputTokens,
    int? LatencyMs,
    string? Retrieval,
    int? InvalidCitations,
    int? DroppedSentences,
    string? ErrorCode,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    bool SourceCoverageTruncated = false,
    IReadOnlyList<AiAgentStep>? Steps = null,
    Guid? ProposalId = null)
{
    /// <summary>A question at <paramref name="ordinal"/>.</summary>
    public static AiMessageRow UserQuestion(Guid id, Guid conversationId, int ordinal, string question, AiMessageScope scope, DateTime createdAt)
        => new(id, conversationId, ordinal, AiMessageRole.User, null, null, question, scope, null, null, null, null, null, null, null, null,
            null, null, null, createdAt, null);

    /// <summary>The answer right after the question: <see cref="AiMessageState.Preparing"/> or, without any source,
    /// <see cref="AiMessageState.NoSources"/> (complete at once). What is known at preparation is stored with it.</summary>
    public static AiMessageRow PreparedAnswer(Guid id, Guid conversationId, int ordinal, string state, string task, string? retrieval,
        string? promptVersion, Guid? profileRevisionId, string? model, DateTime createdAt)
        => new(id, conversationId, ordinal, AiMessageRole.Assistant, state, task, null, null, null, promptVersion, profileRevisionId, model,
            null, null, null, retrieval, null, null, null, createdAt, state == AiMessageState.Preparing ? null : createdAt);

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id).Append(", ConversationId = ").Append(ConversationId).Append(", Ordinal = ").Append(Ordinal)
            .Append(", Role = ").Append(Role).Append(", State = ").Append(State).Append(", Task = ").Append(Task)
            .Append(", ErrorCode = ").Append(ErrorCode);
        return true;
    }
}

/// <summary>The validated answer as stored in <c>AiMessage.Answer</c>:
/// <c>{"answerable":true,"sentences":[{"text":"…","sources":[1,2]}]}</c>. Source numbers are the context ordinals of the message.</summary>
public sealed record AiStoredAnswer(bool Answerable, IReadOnlyList<AiStoredSentence> Sentences)
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, s_json);

    public static AiStoredAnswer FromJson(string json)
        => JsonSerializer.Deserialize<AiStoredAnswer>(json, s_json) ?? throw new InvalidOperationException("Stored answer is empty.");

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Answerable = ").Append(Answerable).Append(", Sentences = ").Append(Sentences.Count);
        return true;
    }
}

/// <summary>One tool call of an agent turn as stored in <c>AiMessage.Steps</c> and shown in a reopened conversation: the tool and
/// whether it succeeded, never its arguments, output or the page it concerned (spec §4.1).</summary>
public sealed record AiAgentStep(string Tool, bool Ok)
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public static string ToJson(IReadOnlyList<AiAgentStep> steps) => JsonSerializer.Serialize(steps, s_json);

    public static IReadOnlyList<AiAgentStep> FromJson(string json) => JsonSerializer.Deserialize<AiAgentStep[]>(json, s_json) ?? [];
}

public sealed record AiStoredSentence(string Text, IReadOnlyList<int> Sources)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Sources = ").Append(string.Join(',', Sources));
        return true;
    }
}

/// <summary>
/// One row of <c>AiSourceDependency</c> (without its message). A page dependency has <see cref="PageId"/> and
/// <see cref="RevisionId"/>; both become null when the revision is deleted (FK ON DELETE SET NULL), which makes the dependency
/// unavailable. Any other <see cref="SourceType"/> has <see cref="ExternalKey"/> and <see cref="ExternalRevision"/> only.
/// <see cref="GenerationId"/> and <see cref="ChunkOrdinal"/> locate the chunk whose text went into the prompt.
/// </summary>
public sealed record AiSourceDependencyRow(
    int Ordinal,
    string Kind,
    string SourceType,
    int SourceVersion,
    Guid? PageId,
    Guid? RevisionId,
    string? ExternalKey,
    string? ExternalRevision,
    Guid? GenerationId,
    int? ChunkOrdinal,
    string? HeadingId,
    bool Cited)
{
    /// <summary>A published page revision, optionally with its chunk.</summary>
    public static AiSourceDependencyRow Page(int ordinal, string kind, Guid pageId, Guid revisionId, Guid? generationId = null,
        int? chunkOrdinal = null, string? headingId = null)
        => new(ordinal, kind, AiPageSource.Type, AiPageSource.Version, pageId, revisionId, null, null, generationId, chunkOrdinal, headingId, false);

    /// <summary>A source of another adapter (AI-11), identified by its own key and revision.</summary>
    public static AiSourceDependencyRow External(int ordinal, string kind, string sourceType, int sourceVersion, string key, string? revision,
        string? anchor = null)
        => new(ordinal, kind, sourceType, sourceVersion, null, null, key, revision, null, null, anchor, false);

    /// <summary>This dependency copied into a follow-up answer as <see cref="AiDependencyKind.History"/> at <paramref name="ordinal"/>.</summary>
    public AiSourceDependencyRow AsHistory(int ordinal) => this with { Ordinal = ordinal, Kind = AiDependencyKind.History, Cited = false };

    /// <summary>The identity of the source regardless of ordinal, kind and citation: copies of one source into history are
    /// deduplicated by it.</summary>
    public (string SourceType, int SourceVersion, Guid? PageId, Guid? RevisionId, string? ExternalKey, string? ExternalRevision, Guid? GenerationId, int? ChunkOrdinal) Identity
        => (SourceType, SourceVersion, PageId, RevisionId, ExternalKey, ExternalRevision, GenerationId, ChunkOrdinal);
}

/// <summary>A message together with the conversation it belongs to (the owner is checked by the read).</summary>
public sealed record AiOwnedMessage(AiConversationRow Conversation, AiMessageRow Message);

/// <summary>One earlier exchange for a follow-up prompt: the question, its answered answer and the answer's dependencies.</summary>
public sealed record AiHistoryTurn(AiMessageRow Question, AiMessageRow Answer, IReadOnlyList<AiSourceDependencyRow> Dependencies);

/// <summary>A listed conversation for "Moje konverzace": its first question (the title source; the caller shortens it and leaves
/// it out when the conversation is not available) and the ids of its answers for one batch of dependency status.</summary>
public sealed record AiConversationListItem(AiConversationRow Conversation, string? FirstQuestion, IReadOnlyList<Guid> AnswerIds)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Conversation = ").Append(Conversation).Append(", Answers = ").Append(AnswerIds.Count);
        return true;
    }
}

/// <summary>How a preparing answer ends (R2: the gate, the check before the model, an outage, a cancellation). Metadata left
/// null keeps what the preparation stored. <see cref="CitedOrdinals"/> marks those context dependencies as cited.</summary>
public sealed record AiAnswerCompletion(
    string State,
    AiStoredAnswer? Answer,
    string? ErrorCode,
    DateTime CompletedAt,
    IReadOnlyCollection<int> CitedOrdinals,
    string? PromptVersion = null,
    Guid? ProfileRevisionId = null,
    string? Model = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    int? LatencyMs = null,
    int? InvalidCitations = null,
    int? DroppedSentences = null,
    IReadOnlyList<AiAgentStep>? Steps = null)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("State = ").Append(State).Append(", ErrorCode = ").Append(ErrorCode).Append(", Cited = ").Append(CitedOrdinals.Count);
        return true;
    }
}
