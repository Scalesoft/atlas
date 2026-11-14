using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Feedback.Commands;
using Atlas.Server.Features.Assistant.Feedback.Contracts;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>Plan phase 3 Q5: the thumb under an answer is one anonymous row per message; the note goes with the message (O20).</summary>
public sealed class AiFeedbackTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Feedback_is_one_row_per_message_without_page_text_and_survives_deletion_anonymously()
    {
        await using var f = await FeedbackFixture.CreateAsync();
        var owner = f.Readers[0];
        var stranger = f.Readers[1];
        var (questionId, answerId, conversationId) = await f.AnswerAsync(owner, "Kde je tiskárna?", AiMessageState.Answered);

        // Only the owner's answer is found: somebody else's and a question id are 404, never 403.
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => Rate(f, stranger, answerId, "up"))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => Rate(f, owner, questionId, "up"))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => Rate(f, owner, Guid.NewGuid(), "up"))).Status);

        await Rate(f, owner, answerId, "up");
        Assert.Equal(["up|"], await f.Strings($"""SELECT "Rating" || '|' || coalesce("Reason",'') FROM "AiFeedback" WHERE {Mine(f)}"""));

        // Saving again overwrites: still one row, now a thumb down with reason and private note.
        await Rate(f, owner, answerId, "down", AiFeedbackReason.Inaccurate, "  Je to jinde.  ");
        Assert.Equal(["down|inaccurate|Je to jinde."], await f.Strings($"""SELECT "Rating" || '|' || "Reason" || '|' || "Note" FROM "AiFeedback" WHERE {Mine(f)}"""));
        Assert.Equal([$"chat.answer|answer@1|{f.ProfileRevisionId}|{answerId}"],
            await f.Strings($"""SELECT "Task" || '|' || "PromptVersion" || '|' || "ProfileRevisionId" || '|' || "MessageId" FROM "AiFeedback" WHERE {Mine(f)}"""));

        // The row holds no person, question, answer or page: only these columns exist.
        Assert.Equal(["CreatedAt", "Id", "MessageId", "Note", "ProfileRevisionId", "PromptVersion", "Rating", "Reason", "Task", "UpdatedAt"],
            (await f.Strings("""
                SELECT CAST(column_name AS text) FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='AiFeedback'
                """)).Order(StringComparer.Ordinal));

        // What is not valid: a thumb up with a reason or note, an unknown rating or reason, a long note.
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => Rate(f, owner, answerId, "up", AiFeedbackReason.Other))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => Rate(f, owner, answerId, "up", null, "x"))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => Rate(f, owner, answerId, "meh"))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => Rate(f, owner, answerId, "down", "bad"))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => Rate(f, owner, answerId, "down", null, new string('x', 501)))).Status);
        Assert.Equal(["down|inaccurate|Je to jinde."], await f.Strings($"""SELECT "Rating" || '|' || "Reason" || '|' || "Note" FROM "AiFeedback" WHERE {Mine(f)}"""));

        // An answer that is not complete (or has no text) cannot be rated.
        var preparing = await f.AnswerAsync(owner, "Co dál?", AiMessageState.Preparing);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => Rate(f, owner, preparing.AnswerId, "up"))).Status);
        var noSources = await f.AnswerAsync(owner, "Co dál ještě?", AiMessageState.NoSources);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => Rate(f, owner, noSources.AnswerId, "up"))).Status);

        // Deleting the conversation removes the message; the rating stays as anonymous operating data, the note goes (O20).
        await f.StoreAsync(store => store.DeleteOwnedAsync(conversationId, owner.Id, default));
        Assert.Equal([$"down|inaccurate|chat.answer|answer@1||"], await f.Strings($"""
            SELECT "Rating" || '|' || "Reason" || '|' || "Task" || '|' || "PromptVersion" || '|' || coalesce(CAST("MessageId" AS text),'') || '|' || coalesce("Note",'')
            FROM "AiFeedback" WHERE {Mine(f)}
            """));
    }

    /// <summary>O20 on every path that deletes messages: the retention of expired conversations (and of all of an owner's) goes through
    /// ConversationStore.DeleteAsync, which clears the notes before the cascade (C# since 3.6, TR_AiMessage_ScrubFeedbackNote before);
    /// a note of a conversation that stays is untouched.</summary>
    [PostgreSqlFact]
    public async Task Retention_scrubs_the_notes_of_the_messages_it_deletes_and_keeps_the_others()
    {
        await using var f = await FeedbackFixture.CreateAsync();
        var owner = f.Readers[0];
        var (_, expired, expiredConversation) = await f.AnswerAsync(owner, "Kde je tiskárna?", AiMessageState.Answered);
        var (_, kept, _) = await f.AnswerAsync(f.Readers[1], "Kde je kuchyňka?", AiMessageState.Answered);
        await Rate(f, owner, expired, "down", AiFeedbackReason.Other, "Soukromá poznámka");
        await Rate(f, f.Readers[1], kept, "down", AiFeedbackReason.Other, "Zůstane");

        await f.StoreAsync(store => store.DeleteAsync([expiredConversation], default));

        Assert.Equal([$"down|other|{kept}|Zůstane", "down|other||"], (await f.Strings($"""
            SELECT "Rating" || '|' || "Reason" || '|' || coalesce(CAST("MessageId" AS text),'') || '|' || coalesce("Note",'') FROM "AiFeedback" WHERE {Mine(f)}
            """)).Order(StringComparer.Ordinal));
    }

    /// <summary>The feedback rows of this test: its answers carry the fixture's own profile revision.</summary>
    private static string Mine(FeedbackFixture f) => $"\"ProfileRevisionId\"='{f.ProfileRevisionId}'";

    private static Task Rate(FeedbackFixture f, UserAccount actor, Guid messageId, string rating, string? reason = null, string? note = null)
    {
        f.Host.Actor.Current = actor;
        return f.Host.InvokeAsync(new RateAiMessage(messageId, new AiFeedbackRequest(rating, reason, note)));
    }
}
