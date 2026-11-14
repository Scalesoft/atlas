using System.Text;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Domain;

namespace Atlas.Server.Features.Assistant.Contracts;

// The web contracts of the answer flow (plan phase 3 §7, §20). Records carrying a question, a sentence or source text print no
// text (PrintMembers): nothing of a question, a source or an answer may reach a log (plan phase 3 §0.3, §3 Logování).

/// <summary><c>POST /api/ai/conversations</c>: the first question of a conversation, from the omnisearch (<c>search</c>, task
/// <c>search.answer</c>) or the assistant panel (<c>assistant</c>, task <c>chat.answer</c>). <see cref="Effort"/>: <c>quick</c>
/// (default), <c>balanced</c>, <c>thorough</c>, <c>deep</c> or <c>max</c> (<see cref="Answering.AnswerEffort"/>). <see cref="Task"/>
/// (assistant only): <c>chat.answer</c> (default) or <c>assistant.agent</c>, the agent with tools (D14, D17).</summary>
public sealed record StartAiConversationRequest(string? Origin, string? Question, AiScope? Scope, string? Effort = null, string? Task = null)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Origin = ").Append(Origin).Append(", Scope = ").Append(Scope).Append(", Effort = ").Append(Effort).Append(", Task = ").Append(Task);
        return true;
    }
}

/// <summary><c>POST /api/ai/conversations/{id}/messages</c>: a follow-up question, task <c>chat.answer</c> (default) or
/// <c>assistant.agent</c>.</summary>
public sealed record AskAiRequest(string? Question, AiScope? Scope, string? Effort = null, string? Task = null)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Scope = ").Append(Scope).Append(", Effort = ").Append(Effort).Append(", Task = ").Append(Task);
        return true;
    }
}

/// <summary>Step 1 of an answer (R1): the stored turn and the sources it will be written from. <see cref="State"/> is
/// <c>preparing</c> (call <c>complete</c> next) or <c>no_sources</c> (finished: nothing readable answers the question).</summary>
public sealed record AiTurnPrepared(Guid ConversationId, Guid MessageId, string State, IReadOnlyList<AiSourceView> Sources, bool Truncated = false);
public sealed record PageSummaryRequest(Guid PageId);

/// <summary>The codes and Czech titles of the answer flow's Problem Details (plan phase 3 §3 Logování, §20).</summary>
public static class AiAnswerProblems
{
    public const string TurnInProgress = "turn_in_progress";
    public const string TurnFinished = "turn_finished";
    public const string ConversationUnavailable = "conversation_unavailable";
    public const string ConversationOutdated = "conversation_outdated";
    public const string Limit = "ai_limit";
    public const string Failed = "ai_failed";
    /// <summary>Not a Problem Details code: the <see cref="AiMessageView.ErrorCode"/> of a failed answer whose source was
    /// published again during generation (R2 step 5); the client asks once more.</summary>
    public const string SourcesChanged = "sources_changed";

    public const string LimitTitle = "Limit využití asistenta byl pro dnešek dosažen.";
    public const string FailedTitle = "Odpověď se nepodařilo připravit.";
    public const string OutdatedTitle = "Tato konverzace používá předchozí nastavení.";
    public const string UnavailableTitle = "Tato konverzace už není dostupná. Začněte novou.";

    public static ApiException InProgress() => new(StatusCodes.Status409Conflict, "Předchozí odpověď se ještě připravuje.", TurnInProgress);
    public static ApiException Finished() => new(StatusCodes.Status409Conflict, "Odpověď už je dokončená.", TurnFinished);
    public static ApiException Unavailable() => new(StatusCodes.Status409Conflict, UnavailableTitle, ConversationUnavailable);
    public static ApiException Outdated() => new(StatusCodes.Status409Conflict, OutdatedTitle, ConversationOutdated);
    public static ApiException LimitReached() => new(StatusCodes.Status429TooManyRequests, LimitTitle, Limit);
    public static ApiException CouldNotPrepare() => new(StatusCodes.Status502BadGateway, FailedTitle, Failed);
}
