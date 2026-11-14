using Npgsql;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Sources;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>Plan phase 3 Q1: the store is the only SQL of conversations; the database itself guards the shape of a message and
/// keeps the real FK of page dependencies (spec RAG §1, §2, §5 point 8).</summary>
public sealed class ConversationStoreTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task A_turn_stores_the_question_a_preparing_answer_and_every_dependency_in_order()
    {
        await using var f = await ConversationFixture.CreateAsync();
        var a = await f.PublishAsync("Zálohování");
        var b = await f.PublishAsync("Obnova");
        var conversation = await f.StartAsync(f.Reader);
        var start = conversation.CreatedAt.AddSeconds(1);

        // Inserted out of order on purpose: the store and the reads keep the ordinals.
        var (question, answer) = await f.TurnAsync(conversation, "Jak obnovím zálohu?",
        [
            AiSourceDependencyRow.Page(2, AiDependencyKind.Context, b.PageId, b.RevisionId, Guid.NewGuid(), 0, "obnova"),
            AiSourceDependencyRow.Page(1, AiDependencyKind.Context, a.PageId, a.RevisionId, Guid.NewGuid(), 3, null),
        ], start);

        var messages = await f.StoreAsync(store => store.MessagesAsync(conversation.Id, f.Reader.Id, default));
        Assert.Equal([(1, AiMessageRole.User, (string?)null), (2, AiMessageRole.Assistant, AiMessageState.Preparing)],
            messages.Select(x => (x.Ordinal, x.Role, x.State)));
        Assert.Equal(("Jak obnovím zálohu?", AiMessageScope.Wiki), (messages[0].Question, messages[0].Scope));
        Assert.Equal(("chat.answer", AnswerRetrieval.Lexical, "answer@1", (DateTime?)null), (messages[1].Task, messages[1].Retrieval, messages[1].PromptVersion, messages[1].CompletedAt));
        Assert.DoesNotContain("zálohu", question.ToString(), StringComparison.Ordinal);
        var dependencies = (await f.StoreAsync(store => store.DependenciesAsync([answer.Id], default)))[answer.Id];
        Assert.Equal([(1, a.PageId, 3), (2, b.PageId, 0)], dependencies.Select(x => (x.Ordinal, x.PageId!.Value, x.ChunkOrdinal!.Value)));
        Assert.All(dependencies, x => Assert.Equal((AiDependencyKind.Context, AiPageSource.Type, 1, false), (x.Kind, x.SourceType, x.SourceVersion, x.Cited)));
        var touched = await f.StoreAsync(store => store.LockOwnedAsync(conversation.Id, f.Reader.Id, default));
        Assert.Equal((1, start), (touched!.Version, touched.LastActivityAt));
        Assert.Null(await f.StoreAsync(store => store.LockOwnedAsync(conversation.Id, f.Writer.Id, default)));
        Assert.True(await f.StoreAsync(store => store.HasPreparingAsync(conversation.Id, default)));

        // The gate releases the answer: only then does it carry sentences, and the cited context sources are marked.
        var released = new AiStoredAnswer(true, [new AiStoredSentence("Zálohu obnovíte z administrace.", [2])]);
        var completion = new AiAnswerCompletion(AiMessageState.Answered, released, null, start.AddSeconds(2), [2], InputTokens: 900, OutputTokens: 40,
            LatencyMs: 1300, InvalidCitations: 1, DroppedSentences: 0);
        Assert.True(await f.StoreAsync(store => store.CompleteAsync(answer.Id, completion, default)));
        Assert.False(await f.StoreAsync(store => store.CompleteAsync(answer.Id, completion with { State = AiMessageState.Withheld, Answer = null }, default)));
        var stored = await f.StoreAsync(store => store.MessageAsync(answer.Id, f.Reader.Id, forUpdate: false, default));
        Assert.Equal((AiMessageState.Answered, 900, 40, 1300, 1, "gemma-4"), (stored!.Message.State, stored.Message.InputTokens, stored.Message.OutputTokens,
            stored.Message.LatencyMs, stored.Message.InvalidCitations, stored.Message.Model));
        Assert.Equal(released.Sentences.Single().Text, stored.Message.Answer!.Sentences.Single().Text);
        Assert.Null(await f.StoreAsync(store => store.MessageAsync(answer.Id, f.Writer.Id, forUpdate: false, default)));
        Assert.Equal([false, true], (await f.StoreAsync(store => store.DependenciesAsync([answer.Id], default)))[answer.Id].Select(x => x.Cited));
        Assert.Equal(1, await f.StoreAsync(store => store.DailyCountAsync(f.Reader.Id, "chat.answer", start.AddHours(-1), start.AddHours(1), default)));

        // A follow-up continues the ordinals; the history window holds the answered exchange with its dependencies.
        await f.TurnAsync(conversation, "A kdy?", [], start.AddSeconds(5));
        var history = await f.StoreAsync(store => store.HistoryAsync(conversation.Id, 3, default));
        var turn = Assert.Single(history);
        Assert.Equal((question.Id, answer.Id, 2), (turn.Question.Id, turn.Answer.Id, turn.Dependencies.Count));
        // no_sources is complete at once and does not count towards the daily limit.
        Assert.Equal(1, await f.StoreAsync(store => store.DailyCountAsync(f.Reader.Id, "chat.answer", start.AddHours(-1), start.AddHours(1), default)));

        // The database refuses a question without text and an answer text outside "answered".
        var refused = await Assert.ThrowsAsync<PostgresException>(() => f.ExecuteAsync("""
            INSERT INTO "AiMessage"("Id","ConversationId","Ordinal","Role","ScopeKind","CreatedAt") VALUES (gen_random_uuid(),@c,10,'user','wiki',now())
            """, ("c", conversation.Id)));
        Assert.Equal(("23514", "CK_AiMessage_Role"), (refused.SqlState, refused.ConstraintName));
        refused = await Assert.ThrowsAsync<PostgresException>(() => f.ExecuteAsync("""
            INSERT INTO "AiMessage"("Id","ConversationId","Ordinal","Role","State","Task","Answer","CreatedAt","CompletedAt")
            VALUES (gen_random_uuid(),@c,10,'assistant','withheld','chat.answer','{"answerable":true,"sentences":[]}',now(),now())
            """, ("c", conversation.Id)));
        Assert.Equal(("23514", "CK_AiMessage_Role"), (refused.SqlState, refused.ConstraintName));
    }

    [PostgreSqlFact]
    public async Task Deleting_a_page_revision_nulls_the_dependency()
    {
        await using var f = await ConversationFixture.CreateAsync();
        var page = await f.PublishAsync("Síť");
        var current = await f.RepublishAsync(page.PageId, "Nová verze.");
        var conversation = await f.StartAsync(f.Reader);
        var (_, answer) = await f.TurnAsync(conversation, "Jak se připojím?",
        [
            AiSourceDependencyRow.Page(1, AiDependencyKind.Context, page.PageId, page.RevisionId),
            AiSourceDependencyRow.Page(2, AiDependencyKind.Context, page.PageId, current),
        ]);

        await f.DeletePageRevisionAsync(page.RevisionId);

        var dependencies = (await f.StoreAsync(store => store.DependenciesAsync([answer.Id], default)))[answer.Id];
        Assert.Equal([((Guid?)null, (Guid?)null), (page.PageId, current)], dependencies.Select(x => (x.PageId, x.RevisionId)));
        // Deleting the conversation removes messages and dependencies, never the page.
        Assert.True(await f.StoreAsync(store => store.DeleteOwnedAsync(conversation.Id, f.Reader.Id, default)));
        Assert.Empty(await f.StoreAsync(store => store.DependenciesAsync([answer.Id], default)));
        Assert.Equal(current, await f.PublishedRevisionAsync(page.PageId));
    }

    /// <summary>The two refusals of CK_AiSourceDependency_Type, which keeps a page reference free of an external key and an external
    /// one free of a page. Kept from the removed DependencyStatusTests: no other test reaches the constraint (SyntheticSourceTests
    /// covers storing an external dependency through the real flow).</summary>
    [PostgreSqlFact]
    public async Task The_database_refuses_a_page_dependency_with_an_external_key_and_an_external_one_with_a_page()
    {
        await using var f = await ConversationFixture.CreateAsync();
        var page = await f.PublishAsync("Poznámky");
        var conversation = await f.StartAsync(f.Reader);
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => f.TurnAsync(conversation, "Smíchaná reference?",
            [AiSourceDependencyRow.Page(1, AiDependencyKind.Context, page.PageId, page.RevisionId) with { ExternalKey = "note-42" }]));
        Assert.Equal("CK_AiSourceDependency_Type", Assert.IsType<PostgresException>(refused.GetBaseException()).ConstraintName);
        refused = await Assert.ThrowsAnyAsync<Exception>(() => f.TurnAsync(conversation, "Smíchaná reference?",
            [AiSourceDependencyRow.External(1, AiDependencyKind.Context, "note", 1, "note-42", "7") with { PageId = page.PageId, RevisionId = page.RevisionId }]));
        Assert.Equal("CK_AiSourceDependency_Type", Assert.IsType<PostgresException>(refused.GetBaseException()).ConstraintName);
    }
}
