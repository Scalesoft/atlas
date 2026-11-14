using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Assistant.Feedback;

/// <summary>The answer a person gives feedback on, found by its owner (anybody else's id is simply not found, 404, never 403).
/// <see cref="Question"/> is the question of the same turn and <see cref="SpaceId"/> the space it was asked in (its scope, else the
/// space of its page); both are only for the report and never leave the handler. <see cref="ToString"/> prints neither.</summary>
public sealed record AiOwnedAnswer(Guid MessageId, string State, string Task, string? PromptVersion, Guid? ProfileRevisionId,
    string? Question, Guid? SpaceId)
{
    public override string ToString() => $"AiOwnedAnswer({MessageId}, {State})";
}

public sealed record AiStoredFeedback(string Rating, string? Reason);

/// <summary>The only SQL of <c>AiFeedback</c> (migration 2026100710). The answer is looked up read-only; nothing here logs.</summary>
public sealed class AiFeedbackStore(ISession session)
{
    /// <summary>The owner's answer message (role assistant) with the question of its turn, or null.</summary>
    public async Task<AiOwnedAnswer?> OwnedAnswerAsync(Guid messageId, Guid ownerId, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery("""
            SELECT a."State",a."Task",a."PromptVersion",a."ProfileRevisionId",q."Question",
              COALESCE(q."ScopeSpaceId",(SELECT p."SpaceId" FROM "Page" p WHERE p."Id"=q."ScopePageId")) AS "SpaceId"
            FROM "AiMessage" a
            JOIN "AiConversation" c ON c."Id"=a."ConversationId" AND c."OwnerId"=:owner
            LEFT JOIN "AiMessage" q ON q."ConversationId"=a."ConversationId" AND q."Ordinal"=a."Ordinal"-1 AND q."Role"='user'
            WHERE a."Id"=:id AND a."Role"='assistant'
            """)
            .AddScalar("State", NHibernateUtil.String).AddScalar("Task", NHibernateUtil.String).AddScalar("PromptVersion", NHibernateUtil.String)
            .AddScalar("ProfileRevisionId", NHibernateUtil.Guid).AddScalar("Question", NHibernateUtil.StringClob).AddScalar("SpaceId", NHibernateUtil.Guid)
            .SetParameter("id", messageId).SetParameter("owner", ownerId).ListAsync<object[]>(ct);
        return rows.Count == 0 ? null
            : new AiOwnedAnswer(messageId, (string)rows[0][0], (string)rows[0][1], (string?)rows[0][2], (Guid?)rows[0][3], (string?)rows[0][4], (Guid?)rows[0][5]);
    }

    /// <summary>One row per message: the first rating creates it, later ones overwrite rating, reason and note.</summary>
    public async Task SaveAsync(AiOwnedAnswer answer, string rating, string? reason, string? note, DateTime now, CancellationToken ct)
        => await session.CreateSQLQuery("""
            INSERT INTO "AiFeedback"("Id","MessageId","Task","PromptVersion","ProfileRevisionId","Rating","Reason","Note","CreatedAt","UpdatedAt")
            VALUES (:id,:message,:task,:prompt,:profile,:rating,:reason,:note,:now,:now)
            ON CONFLICT ("MessageId") WHERE "MessageId" IS NOT NULL
            DO UPDATE SET "Rating"=excluded."Rating","Reason"=excluded."Reason","Note"=excluded."Note","UpdatedAt"=excluded."UpdatedAt"
            """)
            .SetParameter("id", Guid.NewGuid()).SetParameter("message", answer.MessageId).SetParameter("task", answer.Task)
            .SetParameter("prompt", answer.PromptVersion, NHibernateUtil.String).SetParameter("profile", answer.ProfileRevisionId, NHibernateUtil.Guid)
            .SetParameter("rating", rating).SetParameter("reason", reason, NHibernateUtil.String).SetParameter("note", note, NHibernateUtil.String)
            .SetParameter("now", now, NHibernateUtil.UtcDateTime).ExecuteUpdateAsync(ct);

    public async Task<AiStoredFeedback?> GetAsync(Guid messageId, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery("""SELECT "Rating","Reason" FROM "AiFeedback" WHERE "MessageId"=:id""")
            .AddScalar("Rating", NHibernateUtil.String).AddScalar("Reason", NHibernateUtil.String)
            .SetParameter("id", messageId).ListAsync<object[]>(ct);
        return rows.Count == 0 ? null : new AiStoredFeedback((string)rows[0][0], (string?)rows[0][1]);
    }
}
