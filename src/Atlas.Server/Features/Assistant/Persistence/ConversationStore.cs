using NHibernate;
using Atlas.Server.Features.Assistant.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Assistant.Persistence;

/// <summary>
/// The only SQL of conversations, messages and source dependencies (plan phase 3 Q1, migration 2026100700). Native SQL like the
/// knowledge index: the answer is jsonb and the dependencies have a composite FK. Callers own the transaction: one short
/// transaction per step of the answer flow, never one around a model call. Every read of a conversation or a message takes its
/// owner, so another person's id is simply not found (404, never 403). Nothing here logs, and no row prints its text.
/// </summary>
public sealed class ConversationStore(ISession session)
{
    private const int InsertBatch = 50;

    private const string ConversationColumns = """
        c."Id",c."OwnerId",c."Origin",c."Listed",c."ChatProfileRevisionId",c."ChatCompatibility",c."CreatedAt",c."LastActivityAt",c."Version"
        """;

    private const string MessageColumns = """
        m."Id",m."ConversationId",m."Ordinal",m."Role",m."State",m."Task",m."Question",m."ScopeKind",m."ScopeSpaceId",m."ScopePageId",
        CAST(m."Answer" AS text) AS "Answer",m."PromptVersion",m."ProfileRevisionId",m."Model",m."InputTokens",m."OutputTokens",m."LatencyMs",
        m."Retrieval",m."InvalidCitations",m."DroppedSentences",m."ErrorCode",m."CreatedAt",m."CompletedAt",m."SourceCoverageTruncated",
        CAST(m."Steps" AS text) AS "Steps",(SELECT p."Id" FROM "AiToolProposal" p WHERE p."MessageId"=m."Id") AS "ProposalId"
        """;

    // ---- conversations ------------------------------------------------------------------------------------------------

    public async Task InsertConversationAsync(AiConversationRow conversation, CancellationToken ct)
    {
        if (!AiConversationOrigin.IsKnown(conversation.Origin))
        {
            throw new ArgumentException("Unknown conversation origin.", nameof(conversation));
        }
        await session.CreateSQLQuery("""
            INSERT INTO "AiConversation"("Id","OwnerId","Origin","Listed","ChatProfileRevisionId","ChatCompatibility","CreatedAt","LastActivityAt","Version")
            VALUES (:id,:owner,:origin,:listed,:profile,:compatibility,:created,:activity,:version)
            """)
            .SetParameter("id", conversation.Id).SetParameter("owner", conversation.OwnerId).SetParameter("origin", conversation.Origin)
            .SetParameter("listed", conversation.Listed).SetParameter("profile", conversation.ChatProfileRevisionId)
            .SetParameter("compatibility", conversation.ChatCompatibility)
            .SetParameter("created", conversation.CreatedAt, NHibernateUtil.UtcDateTime)
            .SetParameter("activity", conversation.LastActivityAt, NHibernateUtil.UtcDateTime)
            .SetParameter("version", conversation.Version)
            .ExecuteUpdateAsync(ct);
    }

    /// <summary>The owner's conversation locked FOR UPDATE (one turn at a time), or null: missing or somebody else's (404).</summary>
    public Task<AiConversationRow?> LockOwnedAsync(Guid conversationId, Guid ownerId, CancellationToken ct)
        => OwnedAsync(conversationId, ownerId, " FOR UPDATE", ct);

    /// <summary>The owner's conversation without a lock, or null.</summary>
    public Task<AiConversationRow?> GetOwnedAsync(Guid conversationId, Guid ownerId, CancellationToken ct)
        => OwnedAsync(conversationId, ownerId, "", ct);

    private async Task<AiConversationRow?> OwnedAsync(Guid conversationId, Guid ownerId, string lockClause, CancellationToken ct)
    {
        var rows = await ConversationQuery("""WHERE c."Id"=:id AND c."OwnerId"=:owner""" + lockClause)
            .SetParameter("id", conversationId).SetParameter("owner", ownerId).ListAsync<object[]>(ct);
        return rows.Count == 0 ? null : ToConversation(rows[0], 0);
    }

    /// <summary>The owner's listed conversations ("Moje konverzace"), newest activity first, strictly before
    /// <paramref name="before"/> when given. Each carries its first question and, for one status batch, every assistant message's
    /// id. Even an unreleased answer keeps dependencies that protect its question and the conversation title.</summary>
    public async Task<IReadOnlyList<AiConversationListItem>> ListOwnedAsync(Guid ownerId, DateTime? before, int take, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery($"""
            SELECT {ConversationColumns},
              (SELECT q."Question" FROM "AiMessage" q WHERE q."ConversationId"=c."Id" AND q."Role"='user' ORDER BY q."Ordinal" LIMIT 1) AS "FirstQuestion",
              array_to_string(ARRAY(SELECT CAST(a."Id" AS text) FROM "AiMessage" a WHERE a."ConversationId"=c."Id" AND a."Role"='assistant'
                ORDER BY a."Ordinal"),',') AS "Answers"
            FROM "AiConversation" c
            WHERE c."OwnerId"=:owner AND c."Listed" AND (CAST(:before AS timestamptz) IS NULL OR c."LastActivityAt"<:before)
            ORDER BY c."LastActivityAt" DESC,c."Id" DESC
            LIMIT :take
            """)
            .AddConversationScalars().AddScalar("FirstQuestion", NHibernateUtil.String).AddScalar("Answers", NHibernateUtil.String)
            .SetParameter("owner", ownerId).SetParameter("before", before, NHibernateUtil.UtcDateTime).SetParameter("take", take)
            .ListAsync<object[]>(ct);
        return rows.Select(row => new AiConversationListItem(ToConversation(row, 0), (string?)row[9],
            ((string)row[10]).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToArray())).ToArray();
    }

    /// <summary>S6 "Pokračovat v asistentovi": the conversation appears in "Moje konverzace". Idempotent; false = not the owner's.</summary>
    public async Task<bool> SetListedAsync(Guid conversationId, Guid ownerId, CancellationToken ct)
        => await session.CreateSQLQuery("""UPDATE "AiConversation" SET "Listed"=true WHERE "Id"=:id AND "OwnerId"=:owner""")
            .SetParameter("id", conversationId).SetParameter("owner", ownerId).ExecuteUpdateAsync(ct) == 1;

    /// <summary>R5: a compatible current chat revision (same <see cref="ChatProfileCompatibility"/> fingerprint) silently becomes
    /// the pinned one. False when the fingerprint differs, so an incompatible revision can never be pinned by mistake.</summary>
    public async Task<bool> RepinProfileAsync(Guid conversationId, Guid profileRevisionId, string compatibility, CancellationToken ct)
        => await session.CreateSQLQuery("""
            UPDATE "AiConversation" SET "ChatProfileRevisionId"=:profile WHERE "Id"=:id AND "ChatCompatibility"=:compatibility
            """)
            .SetParameter("id", conversationId).SetParameter("profile", profileRevisionId).SetParameter("compatibility", compatibility)
            .ExecuteUpdateAsync(ct) == 1;

    /// <summary>Deletes the owner's conversation with its messages and dependencies (cascade), never a page or revision.</summary>
    public async Task<bool> DeleteOwnedAsync(Guid conversationId, Guid ownerId, CancellationToken ct)
        => await LockOwnedAsync(conversationId, ownerId, ct) is not null && await DeleteAsync([conversationId], ct) == 1;

    /// <summary>The ids of every conversation of the owner, listed or not, locked FOR UPDATE in id order (a running turn finishes
    /// its transaction first), for a deletion that tells its listeners which conversations go.</summary>
    public async Task<IReadOnlyList<Guid>> LockAllOwnedAsync(Guid ownerId, CancellationToken ct)
        => (await session.CreateSQLQuery("""SELECT "Id" FROM "AiConversation" WHERE "OwnerId"=:owner ORDER BY "Id" FOR UPDATE""")
            .AddScalar("Id", NHibernateUtil.Guid).SetParameter("owner", ownerId).ListAsync<Guid>(ct)).ToArray();

    /// <summary>One retention batch (<see cref="AiConversationRetention"/>), locked: unlisted conversations inactive since before
    /// <paramref name="unlistedBefore"/> and listed ones since before <paramref name="listedBefore"/>, oldest first, at most
    /// <paramref name="batch"/>, rows locked by a running turn skipped.</summary>
    public async Task<IReadOnlyList<Guid>> LockExpiredAsync(DateTime unlistedBefore, DateTime listedBefore, int batch, CancellationToken ct)
        => (await session.CreateSQLQuery("""
            SELECT "Id" FROM "AiConversation"
            WHERE (NOT "Listed" AND "LastActivityAt"<:unlisted) OR ("Listed" AND "LastActivityAt"<:listed)
            ORDER BY "LastActivityAt","Id" LIMIT :batch FOR UPDATE SKIP LOCKED
            """)
            .AddScalar("Id", NHibernateUtil.Guid)
            .SetParameter("unlisted", unlistedBefore, NHibernateUtil.UtcDateTime).SetParameter("listed", listedBefore, NHibernateUtil.UtcDateTime)
            .SetParameter("batch", batch).ListAsync<Guid>(ct)).ToArray();

    /// <summary>Deletes these conversations with their messages and dependencies (cascade), never a page or revision. The caller
    /// has locked them (<see cref="LockOwnedAsync"/>, <see cref="LockAllOwnedAsync"/>, <see cref="LockExpiredAsync"/>).</summary>
    public async Task<int> DeleteAsync(IReadOnlyCollection<Guid> conversationIds, CancellationToken ct)
        {
        if (conversationIds.Count == 0) return 0;
        var ids = conversationIds.Distinct().ToArray();
        // Completion locks messages, not conversations. Wait for its final state before taking the accounting snapshot.
        await session.CreateSQLQuery("""
            SELECT "Id" FROM "AiMessage" WHERE "ConversationId" IN (:ids) ORDER BY "Id" FOR UPDATE
            """).AddScalar("Id", NHibernateUtil.Guid).SetParameterList("ids", ids).ListAsync<Guid>(ct);
        // One ordering across the entire batch; per-conversation upserts can deadlock on overlapping days/tasks.
        await session.CreateSQLQuery("""
            INSERT INTO "AiDeletedDailyUsage" ("OwnerId","Task","Day","Count")
            SELECT c."OwnerId",m."Task",date_trunc('day',m."CreatedAt",'UTC'),count(*)
            FROM "AiMessage" m JOIN "AiConversation" c ON c."Id"=m."ConversationId"
            WHERE c."Id" IN (:ids) AND m."Role"='assistant' AND m."State"<>'no_sources'
            GROUP BY c."OwnerId",m."Task",date_trunc('day',m."CreatedAt",'UTC')
            ORDER BY c."OwnerId",m."Task",date_trunc('day',m."CreatedAt",'UTC')
            ON CONFLICT ("OwnerId","Task","Day") DO UPDATE SET "Count"="AiDeletedDailyUsage"."Count"+EXCLUDED."Count"
            """).SetParameterList("ids", ids).ExecuteUpdateAsync(ct);
        // The private note of a thumb goes with its message (O20); the rating stays as anonymous operating data (ON DELETE SET NULL).
        // The one path that deletes messages (IntegrityRulesConventionTests keeps it so); was TR_AiMessage_ScrubFeedbackNote until 3.6.
        await session.CreateSQLQuery("""
            UPDATE "AiFeedback" SET "Note" = NULL
            WHERE "Note" IS NOT NULL AND "MessageId" IN (SELECT "Id" FROM "AiMessage" WHERE "ConversationId" IN (:ids))
            """).SetParameterList("ids", ids).ExecuteUpdateAsync(ct);
        return await session.CreateSQLQuery("""DELETE FROM "AiConversation" WHERE "Id" IN (:ids)""")
            .SetParameterList("ids", ids).ExecuteUpdateAsync(ct);
    }

    // ---- turns --------------------------------------------------------------------------------------------------------

    /// <summary>The ordinal of the next question (1 for an empty conversation). Call it under <see cref="LockOwnedAsync"/>.</summary>
    public async Task<int> NextOrdinalAsync(Guid conversationId, CancellationToken ct)
        => await session.CreateSQLQuery("""SELECT COALESCE(max("Ordinal"),0)+1 AS "Next" FROM "AiMessage" WHERE "ConversationId"=:id""")
            .AddScalar("Next", NHibernateUtil.Int32).SetParameter("id", conversationId).UniqueResultAsync<int>(ct);

    /// <summary>True while an answer of the conversation is still preparing (409 turn_in_progress).</summary>
    public async Task<bool> HasPreparingAsync(Guid conversationId, CancellationToken ct)
        => await session.CreateSQLQuery("""
            SELECT EXISTS (SELECT 1 FROM "AiMessage" WHERE "ConversationId"=:id AND "State"='preparing') AS "Preparing"
            """).AddScalar("Preparing", NHibernateUtil.Boolean).SetParameter("id", conversationId).UniqueResultAsync<bool>(ct);

    /// <summary>
    /// Stores one turn: the question, its answer right after it (<see cref="AiMessageState.Preparing"/>, or
    /// <see cref="AiMessageState.NoSources"/> when nothing was retrieved) and every dependency of the answer. The conversation's
    /// last activity moves to the answer's time and its version grows by one. The conversation must be locked by the caller.
    /// </summary>
    public async Task InsertTurnAsync(AiMessageRow question, AiMessageRow answer, IReadOnlyList<AiSourceDependencyRow> dependencies, CancellationToken ct)
    {
        if (question.Role != AiMessageRole.User || answer.Role != AiMessageRole.Assistant || question.ConversationId != answer.ConversationId
            || answer.Ordinal != question.Ordinal + 1 || answer.State is not (AiMessageState.Preparing or AiMessageState.NoSources))
        {
            throw new ArgumentException("A turn is a question followed by its preparing (or no_sources) answer.");
        }
        await InsertMessageAsync(question, ct);
        await InsertMessageAsync(answer, ct);
        await AddDependenciesAsync(answer.Id, dependencies, ct);
        await session.CreateSQLQuery("""
            UPDATE "AiConversation" SET "LastActivityAt"=GREATEST("LastActivityAt",:at),"Version"="Version"+1 WHERE "Id"=:id
            """).SetParameter("id", answer.ConversationId).SetParameter("at", answer.CreatedAt, NHibernateUtil.UtcDateTime).ExecuteUpdateAsync(ct);
    }

    /// <summary>The dependencies of a preparing answer that are known only after the model (an agent turn's sources and the history
    /// its prompt repeated), stored in the release transaction before <see cref="CompleteAsync"/> marks the cited ones.</summary>
    public async Task AddDependenciesAsync(Guid messageId, IReadOnlyList<AiSourceDependencyRow> dependencies, CancellationToken ct)
    {
        if (dependencies.Select(x => x.Ordinal).Distinct().Count() != dependencies.Count || dependencies.Any(x => x.Cited))
        {
            throw new ArgumentException("Dependency ordinals must be unique, and nothing is cited before the answer is released.", nameof(dependencies));
        }
        foreach (var batch in dependencies.OrderBy(x => x.Ordinal).Chunk(InsertBatch))
        {
            await InsertDependenciesAsync(messageId, batch, ct);
        }
    }

    private async Task InsertMessageAsync(AiMessageRow m, CancellationToken ct)
    {
        await session.CreateSQLQuery("""
            INSERT INTO "AiMessage"("Id","ConversationId","Ordinal","Role","State","Task","Question","ScopeKind","ScopeSpaceId","ScopePageId",
              "Answer","PromptVersion","ProfileRevisionId","Model","InputTokens","OutputTokens","LatencyMs","Retrieval","InvalidCitations",
              "DroppedSentences","ErrorCode","CreatedAt","CompletedAt","SourceCoverageTruncated")
            VALUES (:id,:conversation,:ordinal,:role,:state,:task,:question,:scopeKind,:scopeSpace,:scopePage,CAST(:answer AS jsonb),:prompt,
              :profile,:model,:input,:output,:latency,:retrieval,:invalid,:dropped,:error,:created,:completed,:truncated)
            """)
            .SetParameter("id", m.Id).SetParameter("conversation", m.ConversationId).SetParameter("ordinal", m.Ordinal).SetParameter("role", m.Role)
            .SetParameter("state", m.State, NHibernateUtil.String).SetParameter("task", m.Task, NHibernateUtil.String)
            .SetParameter("question", m.Question, NHibernateUtil.StringClob).SetParameter("scopeKind", m.Scope?.Kind, NHibernateUtil.String)
            .SetParameter("scopeSpace", m.Scope?.SpaceId, NHibernateUtil.Guid).SetParameter("scopePage", m.Scope?.PageId, NHibernateUtil.Guid)
            .SetParameter("answer", m.Answer?.ToJson(), NHibernateUtil.StringClob).SetParameter("prompt", m.PromptVersion, NHibernateUtil.String)
            .SetParameter("profile", m.ProfileRevisionId, NHibernateUtil.Guid).SetParameter("model", m.Model, NHibernateUtil.String)
            .SetParameter("input", m.InputTokens, NHibernateUtil.Int32).SetParameter("output", m.OutputTokens, NHibernateUtil.Int32)
            .SetParameter("latency", m.LatencyMs, NHibernateUtil.Int32).SetParameter("retrieval", m.Retrieval, NHibernateUtil.String)
            .SetParameter("invalid", m.InvalidCitations, NHibernateUtil.Int32).SetParameter("dropped", m.DroppedSentences, NHibernateUtil.Int32)
            .SetParameter("error", m.ErrorCode, NHibernateUtil.String)
            .SetParameter("truncated", m.SourceCoverageTruncated, NHibernateUtil.Boolean).SetParameter("created", m.CreatedAt, NHibernateUtil.UtcDateTime).SetParameter("completed", m.CompletedAt, NHibernateUtil.UtcDateTime)
            .ExecuteUpdateAsync(ct);
    }

    private async Task InsertDependenciesAsync(Guid messageId, IReadOnlyList<AiSourceDependencyRow> rows, CancellationToken ct)
    {
        var values = string.Join(",", rows.Select((_, i) =>
            $"(:m,:o{i},:k{i},:t{i},:v{i},:p{i},:r{i},:ek{i},:er{i},:g{i},:c{i},:h{i},false)"));
        var query = session.CreateSQLQuery($"""
            INSERT INTO "AiSourceDependency"("MessageId","Ordinal","Kind","SourceType","SourceVersion","PageId","RevisionId","ExternalKey",
              "ExternalRevision","GenerationId","ChunkOrdinal","HeadingId","Cited")
            VALUES {values}
            """).SetParameter("m", messageId);
        for (var i = 0; i < rows.Count; i++)
        {
            var d = rows[i];
            query.SetParameter("o" + i, d.Ordinal).SetParameter("k" + i, d.Kind).SetParameter("t" + i, d.SourceType).SetParameter("v" + i, d.SourceVersion)
                .SetParameter("p" + i, d.PageId, NHibernateUtil.Guid).SetParameter("r" + i, d.RevisionId, NHibernateUtil.Guid)
                .SetParameter("ek" + i, d.ExternalKey, NHibernateUtil.String).SetParameter("er" + i, d.ExternalRevision, NHibernateUtil.String)
                .SetParameter("g" + i, d.GenerationId, NHibernateUtil.Guid).SetParameter("c" + i, d.ChunkOrdinal, NHibernateUtil.Int32)
                .SetParameter("h" + i, d.HeadingId, NHibernateUtil.String);
        }
        await query.ExecuteUpdateAsync(ct);
    }

    /// <summary>
    /// Ends a preparing answer in a terminal state (R2: answered after the gate, withheld, failed, canceled, unavailable).
    /// Only <see cref="AiMessageState.Answered"/> may carry the answer (the database refuses anything else). Marks the cited
    /// context dependencies. False when the answer is not preparing any more (already ended, or not an answer at all).
    /// </summary>
    public async Task<bool> CompleteAsync(Guid messageId, AiAnswerCompletion completion, CancellationToken ct)
    {
        if (!AiMessageState.IsTerminal(completion.State))
        {
            throw new ArgumentException("An answer completes in a terminal state.", nameof(completion));
        }
        var updated = await session.CreateSQLQuery("""
            UPDATE "AiMessage" SET "State"=:state,"Answer"=CAST(:answer AS jsonb),"ErrorCode"=:error,"CompletedAt"=:at,
              "PromptVersion"=COALESCE(:prompt,"PromptVersion"),"ProfileRevisionId"=COALESCE(:profile,"ProfileRevisionId"),
              "Model"=COALESCE(:model,"Model"),"InputTokens"=COALESCE(:input,"InputTokens"),"OutputTokens"=COALESCE(:output,"OutputTokens"),
              "LatencyMs"=COALESCE(:latency,"LatencyMs"),"InvalidCitations"=COALESCE(:invalid,"InvalidCitations"),
              "DroppedSentences"=COALESCE(:dropped,"DroppedSentences"),"Steps"=CAST(:steps AS jsonb)
            WHERE "Id"=:id AND "Role"='assistant' AND "State"='preparing'
            """)
            .SetParameter("id", messageId).SetParameter("state", completion.State)
            .SetParameter("answer", completion.Answer?.ToJson(), NHibernateUtil.StringClob).SetParameter("error", completion.ErrorCode, NHibernateUtil.String)
            .SetParameter("at", completion.CompletedAt, NHibernateUtil.UtcDateTime)
            .SetParameter("prompt", completion.PromptVersion, NHibernateUtil.String).SetParameter("profile", completion.ProfileRevisionId, NHibernateUtil.Guid)
            .SetParameter("model", completion.Model, NHibernateUtil.String).SetParameter("input", completion.InputTokens, NHibernateUtil.Int32)
            .SetParameter("output", completion.OutputTokens, NHibernateUtil.Int32).SetParameter("latency", completion.LatencyMs, NHibernateUtil.Int32)
            .SetParameter("invalid", completion.InvalidCitations, NHibernateUtil.Int32).SetParameter("dropped", completion.DroppedSentences, NHibernateUtil.Int32)
            .SetParameter("steps", completion.Steps is { } steps ? AiAgentStep.ToJson(steps) : null, NHibernateUtil.StringClob)
            .ExecuteUpdateAsync(ct);
        if (updated == 0)
        {
            return false;
        }
        if (completion.CitedOrdinals.Count > 0)
        {
            await session.CreateSQLQuery("""
                UPDATE "AiSourceDependency" SET "Cited"=true WHERE "MessageId"=:id AND "Kind"='context' AND "Ordinal" IN (:cited)
                """).SetParameter("id", messageId).SetParameterList("cited", completion.CitedOrdinals.Distinct().ToArray()).ExecuteUpdateAsync(ct);
        }
        return true;
    }

    /// <summary>Answers left preparing before <paramref name="preparedBefore"/> (crashed client, restart) become canceled.</summary>
    public Task<int> CancelStaleAsync(DateTime preparedBefore, DateTime at, CancellationToken ct)
        => session.CreateSQLQuery("""
            WITH stale AS MATERIALIZED (
              SELECT "Id" FROM "AiMessage" WHERE "State"='preparing' AND "CreatedAt"<:before
              ORDER BY "Id" FOR UPDATE SKIP LOCKED)
            UPDATE "AiMessage" m SET "State"='canceled',"CompletedAt"=:at FROM stale WHERE m."Id"=stale."Id"
            """).SetParameter("before", preparedBefore, NHibernateUtil.UtcDateTime).SetParameter("at", at, NHibernateUtil.UtcDateTime).ExecuteUpdateAsync(ct);

    /// <summary>One retention batch (<see cref="AiConversationRetention"/>): unlisted conversations inactive since before
    /// <paramref name="unlistedBefore"/> and listed ones since before <paramref name="listedBefore"/>, at most
    /// <paramref name="batch"/>, rows locked by a running turn skipped. Returns how many went; the caller repeats in new transactions.
    /// The retention of Q4 goes through <see cref="LockExpiredAsync"/> and the deletion listeners instead.</summary>
    public async Task<int> PurgeAsync(DateTime unlistedBefore, DateTime listedBefore, int batch, CancellationToken ct)
        => await DeleteAsync(await LockExpiredAsync(unlistedBefore, listedBefore, batch, ct), ct);

    // ---- messages -----------------------------------------------------------------------------------------------------

    /// <summary>A message of the owner's conversation, the message row locked FOR UPDATE when asked; null = not found or not the
    /// owner's (404).</summary>
    public async Task<AiOwnedMessage?> MessageAsync(Guid messageId, Guid ownerId, bool forUpdate, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery($"""
            SELECT {MessageColumns}
            FROM "AiMessage" m JOIN "AiConversation" c ON c."Id"=m."ConversationId"
            WHERE m."Id"=:id AND c."OwnerId"=:owner{(forUpdate ? " FOR UPDATE OF m" : "")}
            """).AddMessageScalars().SetParameter("id", messageId).SetParameter("owner", ownerId).ListAsync<object[]>(ct);
        if (rows.Count == 0)
        {
            return null;
        }
        var message = ToMessage(rows[0], 0);
        var conversation = await GetOwnedAsync(message.ConversationId, ownerId, ct);
        return conversation is null ? null : new AiOwnedMessage(conversation, message);
    }

    /// <summary>Every message of the owner's conversation in order; empty when it is not the owner's.</summary>
    public async Task<IReadOnlyList<AiMessageRow>> MessagesAsync(Guid conversationId, Guid ownerId, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery($"""
            SELECT {MessageColumns}
            FROM "AiMessage" m JOIN "AiConversation" c ON c."Id"=m."ConversationId"
            WHERE m."ConversationId"=:id AND c."OwnerId"=:owner
            ORDER BY m."Ordinal"
            """).AddMessageScalars().SetParameter("id", conversationId).SetParameter("owner", ownerId).ListAsync<object[]>(ct);
        return rows.Select(row => ToMessage(row, 0)).ToArray();
    }

    /// <summary>
    /// The history window of a follow-up (R6, O18): the last <paramref name="maxTurns"/> answered exchanges, oldest first, each
    /// with its question and every dependency of its answer (context and history), ready to be copied as history. Answers that did
    /// not end answered carry no sentences and are not part of the window.
    /// </summary>
    public async Task<IReadOnlyList<AiHistoryTurn>> HistoryAsync(Guid conversationId, int maxTurns, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery($"""
            WITH answers AS (
              SELECT a."Ordinal" FROM "AiMessage" a
              WHERE a."ConversationId"=:id AND a."Role"='assistant' AND a."State"='answered'
              ORDER BY a."Ordinal" DESC LIMIT :turns)
            SELECT {MessageColumns}
            FROM "AiMessage" m
            WHERE m."ConversationId"=:id AND (m."Ordinal" IN (SELECT "Ordinal" FROM answers) OR m."Ordinal" IN (SELECT "Ordinal"-1 FROM answers))
            ORDER BY m."Ordinal"
            """).AddMessageScalars().SetParameter("id", conversationId).SetParameter("turns", maxTurns).ListAsync<object[]>(ct);
        var messages = rows.Select(row => ToMessage(row, 0)).ToDictionary(x => x.Ordinal);
        var answers = messages.Values.Where(x => x.Role == AiMessageRole.Assistant).OrderBy(x => x.Ordinal).ToArray();
        var dependencies = await DependenciesAsync(answers.Select(x => x.Id).ToArray(), ct);
        return answers
            .Where(answer => messages.TryGetValue(answer.Ordinal - 1, out var question) && question.Role == AiMessageRole.User)
            .Select(answer => new AiHistoryTurn(messages[answer.Ordinal - 1], answer, dependencies.GetValueOrDefault(answer.Id) ?? []))
            .ToArray();
    }

    /// <summary>The answers of the owner counted towards the daily limit of <paramref name="task"/> in [<paramref name="from"/>,
    /// <paramref name="to"/>): every answer except no_sources (plan phase 3 Q3, denní limit).</summary>
    public async Task<long> DailyCountAsync(Guid ownerId, string task, DateTime from, DateTime to, CancellationToken ct)
        => await session.CreateSQLQuery("""
            SELECT (SELECT count(*) FROM "AiMessage" m JOIN "AiConversation" c ON c."Id"=m."ConversationId"
                    WHERE c."OwnerId"=:owner AND m."Role"='assistant' AND m."Task"=:task AND m."State"<>'no_sources'
                      AND m."CreatedAt">=:from AND m."CreatedAt"<:to)
                 + (SELECT CAST(COALESCE(sum("Count"),0) AS bigint) FROM "AiDeletedDailyUsage"
                    WHERE "OwnerId"=:owner AND "Task"=:task AND "Day">=:from AND "Day"<:to) AS "Count"
            """).AddScalar("Count", NHibernateUtil.Int64)
            .SetParameter("owner", ownerId).SetParameter("task", task)
            .SetParameter("from", from, NHibernateUtil.UtcDateTime).SetParameter("to", to, NHibernateUtil.UtcDateTime)
            .UniqueResultAsync<long>(ct);

    // ---- dependencies -------------------------------------------------------------------------------------------------

    /// <summary>The dependencies of the given messages in ordinal order; a message without any has no entry.</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AiSourceDependencyRow>>> DependenciesAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken ct)
    {
        if (messageIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<AiSourceDependencyRow>>();
        }
        var rows = await session.CreateSQLQuery("""
            SELECT "MessageId","Ordinal","Kind","SourceType","SourceVersion","PageId","RevisionId","ExternalKey","ExternalRevision",
              "GenerationId","ChunkOrdinal","HeadingId","Cited"
            FROM "AiSourceDependency" WHERE "MessageId" IN (:ids) ORDER BY "MessageId","Ordinal"
            """)
            .AddScalar("MessageId", NHibernateUtil.Guid).AddScalar("Ordinal", NHibernateUtil.Int16).AddScalar("Kind", NHibernateUtil.String)
            .AddScalar("SourceType", NHibernateUtil.String).AddScalar("SourceVersion", NHibernateUtil.Int16).AddScalar("PageId", NHibernateUtil.Guid)
            .AddScalar("RevisionId", NHibernateUtil.Guid).AddScalar("ExternalKey", NHibernateUtil.String).AddScalar("ExternalRevision", NHibernateUtil.String)
            .AddScalar("GenerationId", NHibernateUtil.Guid).AddScalar("ChunkOrdinal", NHibernateUtil.Int16).AddScalar("HeadingId", NHibernateUtil.String)
            .AddScalar("Cited", NHibernateUtil.Boolean)
            .SetParameterList("ids", messageIds.Distinct().ToArray()).ListAsync<object[]>(ct);
        return rows.GroupBy(row => (Guid)row[0]).ToDictionary(group => group.Key, group => (IReadOnlyList<AiSourceDependencyRow>)group.Select(row =>
            new AiSourceDependencyRow((short)row[1], (string)row[2], (string)row[3], (short)row[4], (Guid?)row[5], (Guid?)row[6], (string?)row[7],
                (string?)row[8], (Guid?)row[9], (short?)row[10], (string?)row[11], (bool)row[12])).ToArray());
    }

    // ---- mapping ------------------------------------------------------------------------------------------------------

    private ISQLQuery ConversationQuery(string where) => session.CreateSQLQuery($"""SELECT {ConversationColumns} FROM "AiConversation" c {where}""")
        .AddConversationScalars();

    private static AiConversationRow ToConversation(object[] row, int at) => new((Guid)row[at], (Guid)row[at + 1], (string)row[at + 2],
        (bool)row[at + 3], (Guid)row[at + 4], (string)row[at + 5], (DateTime)row[at + 6], (DateTime)row[at + 7], (int)row[at + 8]);

    private static AiMessageRow ToMessage(object[] row, int at)
    {
        var scopeKind = (string?)row[at + 7];
        var answer = (string?)row[at + 10];
        var steps = (string?)row[at + 24];
        return new AiMessageRow((Guid)row[at], (Guid)row[at + 1], (short)row[at + 2], (string)row[at + 3], (string?)row[at + 4], (string?)row[at + 5],
            (string?)row[at + 6], scopeKind is null ? null : new AiMessageScope(scopeKind, (Guid?)row[at + 8], (Guid?)row[at + 9]),
            answer is null ? null : AiStoredAnswer.FromJson(answer), (string?)row[at + 11], (Guid?)row[at + 12], (string?)row[at + 13],
            (int?)row[at + 14], (int?)row[at + 15], (int?)row[at + 16], (string?)row[at + 17], (short?)row[at + 18], (short?)row[at + 19],
            (string?)row[at + 20], (DateTime)row[at + 21], (DateTime?)row[at + 22], (bool)row[at + 23],
            steps is null ? null : AiAgentStep.FromJson(steps), (Guid?)row[at + 25]);
    }
}

internal static class ConversationScalars
{
    public static ISQLQuery AddConversationScalars(this ISQLQuery query) => query
        .AddScalar("Id", NHibernateUtil.Guid).AddScalar("OwnerId", NHibernateUtil.Guid).AddScalar("Origin", NHibernateUtil.String)
        .AddScalar("Listed", NHibernateUtil.Boolean).AddScalar("ChatProfileRevisionId", NHibernateUtil.Guid)
        .AddScalar("ChatCompatibility", NHibernateUtil.String).AddScalar("CreatedAt", NHibernateUtil.UtcDateTime)
        .AddScalar("LastActivityAt", NHibernateUtil.UtcDateTime).AddScalar("Version", NHibernateUtil.Int32);

    public static ISQLQuery AddMessageScalars(this ISQLQuery query) => query
        .AddScalar("Id", NHibernateUtil.Guid).AddScalar("ConversationId", NHibernateUtil.Guid).AddScalar("Ordinal", NHibernateUtil.Int16)
        .AddScalar("Role", NHibernateUtil.String).AddScalar("State", NHibernateUtil.String).AddScalar("Task", NHibernateUtil.String)
        .AddScalar("Question", NHibernateUtil.StringClob).AddScalar("ScopeKind", NHibernateUtil.String).AddScalar("ScopeSpaceId", NHibernateUtil.Guid)
        .AddScalar("ScopePageId", NHibernateUtil.Guid).AddScalar("Answer", NHibernateUtil.StringClob).AddScalar("PromptVersion", NHibernateUtil.String)
        .AddScalar("ProfileRevisionId", NHibernateUtil.Guid).AddScalar("Model", NHibernateUtil.String).AddScalar("InputTokens", NHibernateUtil.Int32)
        .AddScalar("OutputTokens", NHibernateUtil.Int32).AddScalar("LatencyMs", NHibernateUtil.Int32).AddScalar("Retrieval", NHibernateUtil.String)
        .AddScalar("InvalidCitations", NHibernateUtil.Int16).AddScalar("DroppedSentences", NHibernateUtil.Int16).AddScalar("ErrorCode", NHibernateUtil.String)
        .AddScalar("CreatedAt", NHibernateUtil.UtcDateTime).AddScalar("CompletedAt", NHibernateUtil.UtcDateTime).AddScalar("SourceCoverageTruncated", NHibernateUtil.Boolean)
        .AddScalar("Steps", NHibernateUtil.StringClob).AddScalar("ProposalId", NHibernateUtil.Guid);
}
