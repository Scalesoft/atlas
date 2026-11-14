using System.Text;
using Atlas.Server.Features.Assistant.Answering;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Search.Domain;

namespace Atlas.Server.Features.Assistant.Commands;

// The steps of one answer (plan phase 3 R1, R2, §7), each one short transaction; the model is called by AiAnswerRunner between
// BeginAiCompletion and ReleaseAiAnswer, outside any transaction and lock. These messages carry a question, source text or an
// answer, so they live in a …Commands namespace (AtlasMessageLogging silences Wolverine's failure log of their category) and
// print no text (PrintMembers): Wolverine reports a failed invocation with ToString.

/// <summary>
/// Step 1 (preparation): authorization of the scope, for a follow-up the owner's conversation (one turn at a time, a compatible
/// chat profile R5, every earlier dependency still available), the daily limit, retrieval through the source adapters, and the
/// stored question, answer (<c>preparing</c> or <c>no_sources</c>) and dependencies. <see cref="Vector"/> is the question's
/// embedding made by the endpoint before the transaction (null = lexical). The chat profile fields are the current revision the
/// endpoint's gate allowed.
/// </summary>
public sealed record PrepareAiTurn(Guid? ConversationId, string Origin, string Task, string Question, AiScope Scope, SemanticQuery? Vector,
    Guid ProfileRevisionId, string ChatCompatibility, string Model, int? DailyLimit, AnswerEffort? Effort = null)
{
    /// <summary>The reranker's order of the answer's pool (<see cref="Queries.RetrieveAnswerCandidates"/>), best first; null = today's retrieval.</summary>
    public IReadOnlyList<Sources.SourceReference>? Preferred { get; init; }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ConversationId = ").Append(ConversationId).Append(", Origin = ").Append(Origin).Append(", Task = ").Append(Task)
            .Append(", Scope = ").Append(Scope).Append(", Hybrid = ").Append(Vector is not null).Append(", Effort = ").Append(Effort?.Name);
        return true;
    }
}

/// <summary>
/// Step 2a (the check before the model, R2 steps 2–4 without writing an answer): the owner's preparing answer, the chat profile
/// still compatible with the conversation (R5), every dependency authorized again and the exact source texts still current. Ends
/// the answer without any model call when it may not be written (<see cref="AiCompletionStart.Finished"/>).
/// </summary>
public sealed record BeginAiCompletion(Guid MessageId, string ChatCompatibility);

/// <summary>Either the finished answer (withheld, failed, canceled: no model call) or the prompt input.</summary>
public sealed record AiCompletionStart(AiMessageView? Finished, AnswerPromptInput? Input)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Finished = ").Append(Finished?.State).Append(", Sources = ").Append(Input?.Sources.Count);
        return true;
    }
}

/// <summary>What the prompt is built from: the question, the authorized source texts in the order of their numbers and the
/// history window (validated sentences of earlier answers, without their source numbers). An agent turn has no sources and gets
/// the question's <see cref="Scope"/> and the conversation's <see cref="EarlierQuestions"/> (oldest first) for its tool router.</summary>
public sealed record AnswerPromptInput(string Question, IReadOnlyList<AnswerPromptSource> Sources, IReadOnlyList<AnswerHistoryTurn> History,
    AiMessageScope? Scope = null, IReadOnlyList<string>? EarlierQuestions = null)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Sources = ").Append(Sources.Count).Append(", History = ").Append(History.Count);
        return true;
    }
}

/// <summary>The operating metadata of one model call: never content (plan phase 3 §0.3).</summary>
public sealed record AiAnswerMetrics(string PromptVersion, Guid ProfileRevisionId, string Model, int? InputTokens, int? OutputTokens, int LatencyMs);

/// <summary>
/// Step 2b, the revocation gate (R2): one short transaction after the model has answered. Every dependency is authorized again
/// under the readers' locks; a lost access, publication, trash or inclusion in AI withholds the answer (its text is never
/// stored), a newer publication of a context source fails it with <c>sources_changed</c>, otherwise the validated answer is
/// stored. The release is this transaction's commit.
/// </summary>
public sealed record ReleaseAiAnswer(Guid MessageId, ValidatedAnswer Answer, AiAnswerMetrics Metrics, AiProvenanceView Provenance)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("MessageId = ").Append(MessageId).Append(", Answerable = ").Append(Answer.Answerable)
            .Append(", Sentences = ").Append(Answer.Sentences.Count).Append(", Metrics = ").Append(Metrics);
        return true;
    }
}

/// <summary>
/// The revocation gate of an agent turn (WP-14), as <see cref="ReleaseAiAnswer"/>: the sources (pages the tools returned, in the
/// order of their numbers) become context dependencies on their current published revision, the history window the prompt repeated
/// becomes history dependencies, and the answer is stored with its steps (and the turn's write proposal, if any) only when every
/// dependency still passes the gate.
/// </summary>
public sealed record ReleaseAgentAnswer(Guid MessageId, Agent.AgentAnswerResult Answer, IReadOnlyList<AiAgentStep> Steps, AiAnswerMetrics Metrics,
    Agent.AiProposalDraft? Proposal = null)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("MessageId = ").Append(MessageId).Append(", Sentences = ").Append(Answer.Answer.Sentences.Count)
            .Append(", Proposal = ").Append(Proposal?.Items.Count)
            .Append(", Sources = ").Append(Answer.Sources.Count).Append(", Steps = ").Append(Steps.Count).Append(", Metrics = ").Append(Metrics);
        return true;
    }
}

/// <summary>Ends a preparing answer without a release: the client went away (<c>canceled</c>), the provider failed
/// (<c>unavailable</c>, <c>failed</c> with the <c>AiOutcome</c> code). Nothing else is written. A no-op once the answer ended.</summary>
public sealed record AbandonAiTurn(Guid MessageId, string State, string? ErrorCode);
