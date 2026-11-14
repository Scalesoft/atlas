using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Sources;

namespace Atlas.Server.Features.Assistant.Answering;

/// <summary>The outcome of authorizing an answer's dependencies again (R2): <see cref="Withheld"/> beats <see cref="Changed"/>.</summary>
public enum AnswerGateOutcome { Allowed, Withheld, Changed }

/// <summary>The decision and, for every dependency (by ordinal), its stored reference: what a released answer is described from.</summary>
public sealed record AnswerGateDecision(AnswerGateOutcome Outcome, IReadOnlyDictionary<int, SourceReference> References);

/// <summary>
/// The check before the model and the revocation gate (plan phase 3 R2), shared by <c>BeginAiCompletion</c> and
/// <c>ReleaseAiAnswer</c>, inside their transaction. Every dependency of the answer goes through its adapter in one call, so the
/// page adapter takes its locks once and in its order (the shared content lock, the spaces FOR SHARE by id, the account FOR SHARE,
/// the pages FOR SHARE by id): a revocation committed before these locks is seen, one that starts after them waits for this
/// transaction. A <c>context</c> dependency needs the exact revision (a newer publication is <see cref="AnswerGateOutcome.Changed"/>),
/// a <c>history</c> one only access and publication. A dependency no adapter vouches for, any unavailable one and a revoked
/// credential (the 401 of the actor lock, also checked when no adapter locked the account) withhold the answer. Never branches on
/// a source type (AI-11).
/// </summary>
public sealed class AnswerTurnGate(SourceRegistry registry, IContentAccess access, Atlas.Server.Features.Ai.Access.AiPolicyResolver policy)
{
    public async Task<AnswerGateDecision> DecideAsync(IReadOnlyList<AiSourceDependencyRow> dependencies, CancellationToken ct)
    {
        var references = new Dictionary<int, SourceReference>();
        foreach (var dependency in dependencies)
        {
            if (registry.Reference(AnswerSourceColumns.Of(dependency)) is not { } reference)
            {
                return new AnswerGateDecision(AnswerGateOutcome.Withheld, references);
            }
            references[dependency.Ordinal] = reference;
        }
        IReadOnlyDictionary<SourceReference, SourceStatus> statuses;
        try
        {
            statuses = await registry.AuthorizeAsync(references.Values.Distinct().ToArray(), requireCurrentRevision: true, ct);
            var actor = await access.LockActorAsync(null, ct);
            if (!(await policy.ResolveAsync(actor.Id, ct)).Enabled)
                return new AnswerGateDecision(AnswerGateOutcome.Withheld, references);
        }
        catch (ApiException e) when (e.Status == StatusCodes.Status401Unauthorized)
        {
            return new AnswerGateDecision(AnswerGateOutcome.Withheld, references);
        }
        var changed = false;
        foreach (var dependency in dependencies)
        {
            switch (statuses[references[dependency.Ordinal]])
            {
                case SourceStatus.Unavailable:
                    return new AnswerGateDecision(AnswerGateOutcome.Withheld, references);
                case SourceStatus.Changed when dependency.Kind == AiDependencyKind.Context:
                    changed = true;
                    break;
            }
        }
        return new AnswerGateDecision(changed ? AnswerGateOutcome.Changed : AnswerGateOutcome.Allowed, references);
    }
}

/// <summary>Between the stored dependency row (Q1) and the adapters' typed columns (Q2).</summary>
public static class AnswerSourceColumns
{
    public static SourceColumns Of(AiSourceDependencyRow row)
        => new(row.SourceType, row.SourceVersion, row.PageId, row.RevisionId, row.ExternalKey, row.ExternalRevision, row.GenerationId,
            row.ChunkOrdinal, row.HeadingId);

    public static AiSourceDependencyRow Row(int ordinal, string kind, SourceColumns columns)
        => new(ordinal, kind, columns.SourceType, columns.SourceVersion, columns.PageId, columns.RevisionId, columns.ExternalKey,
            columns.ExternalRevision, columns.GenerationId, columns.ChunkOrdinal, columns.HeadingId, false);
}

/// <summary>The history window of a follow-up (R6, O18): the last three answered exchanges, the oldest dropped until questions and
/// sentences fit 4 000 characters, exactly as <see cref="AnswerPromptBuilder"/> trims them, so the copied <c>history</c>
/// dependencies are those of the exchanges the prompt repeats.</summary>
public static class AnswerHistoryWindow
{
    public static IReadOnlyList<AiHistoryTurn> Select(IReadOnlyList<AiHistoryTurn> turns)
    {
        var window = turns.TakeLast(AnswerPromptBuilder.MaxHistoryTurns).ToList();
        while (window.Count > 0 && window.Sum(Length) > AnswerPromptBuilder.MaxHistoryLength)
        {
            window.RemoveAt(0);
        }
        return window;
    }

    public static IReadOnlyList<AnswerHistoryTurn> Prompt(IReadOnlyList<AiHistoryTurn> window)
        => window.Select(turn => new AnswerHistoryTurn(turn.Question.Question ?? "", Sentences(turn))).ToArray();

    private static int Length(AiHistoryTurn turn) => (turn.Question.Question ?? "").Length + Sentences(turn).Sum(sentence => sentence.Length);

    private static IReadOnlyList<string> Sentences(AiHistoryTurn turn) => turn.Answer.Answer?.Sentences.Select(sentence => sentence.Text).ToArray() ?? [];
}

/// <summary>The views of an answer message (plan phase 3 §7 contracts).</summary>
public static class AnswerViews
{
    /// <summary>An answer: sentences only when answered; sources only when answered or without an answer from the model
    /// (no_sources); withheld, failed and canceled answers show neither.</summary>
    public static AiMessageView Answer(AiMessageRow message, string state, string? errorCode, AiStoredAnswer? answer,
        IReadOnlyList<AiSourceView> sources, AiProvenanceView? provenance)
        => new(message.Id, message.ConversationId, AiMessageRole.Assistant, state, null, null,
            state == AiMessageState.Answered && answer is not null
                ? answer.Sentences.Select(sentence => new AiSentenceView(sentence.Text, sentence.Sources)).ToArray()
                : [],
            state is AiMessageState.Answered or AiMessageState.NoSources ? sources : [],
            state is AiMessageState.Answered or AiMessageState.NoSources ? provenance : null,
            message.CreatedAt, null, errorCode, message.SourceCoverageTruncated);

}
