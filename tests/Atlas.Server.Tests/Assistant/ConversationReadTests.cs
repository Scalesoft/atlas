using System.Text.Json;
using Npgsql;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Assistant.Commands;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Queries;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Features.Assistant.Persistence;
using Xunit;
using static Atlas.Server.Tests.Assistant.ConversationSeeds;

namespace Atlas.Server.Tests.Assistant;

/// <summary>
/// Reading history (plan phase 3 §8, R4, R5; spec RAG §4, §9 Historie): every read revalidates every source of every answer with
/// the current rules, so an answer whose source the owner can no longer read is withheld without text, sources or question, and
/// the conversation loses its title in the list; conversations are private to their owner; an answer from the omnisearch joins
/// "Moje konverzace" only through Continue, without a model call.
/// </summary>
public sealed class ConversationReadTests
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task History_is_revalidated_on_every_read()
    {
        await using var f = await AnswerSourcesFixture.CreateAsync();
        var manual = await f.PublishAsync("Návod pro správce", "## Postup obnovy\nSpusťte nástroj a vyberte bod v čase.", null, ("Postup obnovy", "postup"));
        var servers = await f.PublishAsync("Provoz serverů", "## Noční kopie\nBěží ve dvě ráno.", null, ("Noční kopie", "nocni"));
        await f.IndexAsync();
        var chat = await SeedChatRevisionAsync(f.Index.Host, f.Admin);
        var conversation = await StartAsync(f.Index.Host, f.Reader, chat);
        await AnsweredAsync(f.Index.Host, conversation, "Jak obnovím zálohu?",
            [await IndexedSourceAsync(f, 1, manual, "postup"), await IndexedSourceAsync(f, 2, servers, "nocni")],
            Answer(("Spusťte nástroj a vyberte bod v čase.", [1])));

        var read = await f.Index.Host.As<AiConversationView>(f.Reader, new GetAiConversation(conversation.Id));
        Assert.Equal((true, false, false), (read.Available, read.CanContinue, read.Outdated));
        Assert.Equal(new AiProvenanceView("company", "LM Studio", Model), read.Provenance);
        Assert.Equal([(AiMessageRole.User, (string?)null, "Jak obnovím zálohu?"), (AiMessageRole.Assistant, AiMessageState.Answered, null)],
            read.Messages.Select(x => (x.Role, x.State, x.Question)));
        var answer = read.Messages[1];
        Assert.Equal("Spusťte nástroj a vyberte bod v čase.", Assert.Single(answer.Sentences).Text);
        Assert.Equal(new AiProvenanceView("company", "LM Studio", Model), answer.Provenance);
        Assert.Equal(
            [
                (1, "page", (Guid?)manual, "Návod pro správce", "Engineering", "Postup obnovy", "postup", "Spusťte nástroj a vyberte bod v čase.", true, false),
                (2, "page", servers, "Provoz serverů", "Engineering", "Noční kopie", "nocni", "Běží ve dvě ráno.", false, false),
            ],
            answer.Sources.Select(x => (x.Number, x.Type, x.PageId, x.Title, x.SpaceName, x.HeadingTitle, x.HeadingId, x.Excerpt, x.Cited, x.Changed)));
        var listed = Assert.Single((await f.Index.Host.As<AiConversationList>(f.Reader, new ListAiConversations())).Items);
        Assert.Equal((conversation.Id, "Jak obnovím zálohu?", true), (listed.Id, listed.Title, listed.Available));

        // The owner loses the first source: the answer goes, with its question and with the source the owner can still read.
        await f.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=@id""", ("id", manual));
        var hidden = await f.Index.Host.As<AiConversationView>(f.Reader, new GetAiConversation(conversation.Id));
        Assert.Equal((false, false), (hidden.Available, hidden.CanContinue));
        Assert.Equal([(AiMessageRole.User, (string?)null, (string?)null), (AiMessageRole.Assistant, AiMessageState.Withheld, null)],
            hidden.Messages.Select(x => (x.Role, x.State, x.Question)));
        Assert.True(hidden.Messages.All(x => x.Sentences.Count == 0 && x.Sources.Count == 0 && x.Provenance is null && x.Scope is null));
        var hiddenList = Assert.Single((await f.Index.Host.As<AiConversationList>(f.Reader, new ListAiConversations())).Items);
        Assert.Equal((null, false), (hiddenList.Title, hiddenList.Available));
        var serialized = JsonSerializer.Serialize(hidden, s_json) + JsonSerializer.Serialize(hiddenList, s_json);
        foreach (var leak in new[] { "Návod", "Provoz", "Spusťte", "Běží", "obnovím", "postup", manual.ToString(), servers.ToString() })
        {
            Assert.DoesNotContain(leak, serialized, StringComparison.Ordinal);
        }

        // Access back: available again on the next read, nothing was rewritten.
        await f.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=false WHERE "Id"=@id""", ("id", manual));
        Assert.Equal(read, await f.Index.Host.As<AiConversationView>(f.Reader, new GetAiConversation(conversation.Id)), ViewComparer.Instance);

        // A newer publication of a source is "Zdroj se změnil", not a withheld answer: the current title, no heading or text of
        // the older revision.
        await f.Index.EditAndPublishAsync(servers, "Provoz serverů 2026", "## Noční kopie\nBěží ve tři ráno.", ("Noční kopie", "nocni"));
        var changed = await f.Index.Host.As<AiConversationView>(f.Reader, new GetAiConversation(conversation.Id));
        Assert.True(changed.Available);
        Assert.Equal(AiMessageState.Answered, changed.Messages[1].State);
        Assert.Equal([(1, "Návod pro správce", (string?)"Postup obnovy", false), (2, "Provoz serverů 2026", null, true)],
            changed.Messages[1].Sources.Select(x => (x.Number, x.Title, x.HeadingTitle, x.Changed)));
        Assert.Null(changed.Messages[1].Sources[1].Excerpt);
    }

    /// <summary>Deleting a page revision nulls the dependencies on it (the FK), so the history read can no longer name that source:
    /// the answer is withheld, as one whose source the owner can no longer read.</summary>
    [PostgreSqlFact]
    public async Task An_answer_whose_source_revision_was_deleted_is_withheld()
    {
        await using var f = await ConversationFixture.CreateAsync();
        var page = await f.PublishAsync("Síť");
        var current = await f.RepublishAsync(page.PageId, "Nová verze.");
        var conversation = await f.StartAsync(f.Reader);
        await AnsweredAsync(f.Host, conversation, "Jak se připojím?", [Source(1, page), Source(2, (page.PageId, current))], Answer(("Přes VPN.", [2])));
        var before = await f.Host.As<AiConversationView>(f.Reader, new GetAiConversation(conversation.Id));
        Assert.True(before.Available);
        Assert.Equal([true, false], before.Messages[1].Sources.Select(x => x.Changed));

        await f.DeletePageRevisionAsync(page.RevisionId);

        var view = await f.Host.As<AiConversationView>(f.Reader, new GetAiConversation(conversation.Id));
        Assert.False(view.Available);
        Assert.Equal(AiMessageState.Withheld, view.Messages[1].State);
        Assert.Null(view.Messages[0].Question);
        Assert.All(view.Messages, message => Assert.Empty(message.Sources));
    }

    [PostgreSqlFact]
    public async Task Conversations_are_private_to_their_owner()
    {
        await using var f = await ConversationFixture.CreateAsync();
        var page = await f.PublishAsync("Síť");
        var conversation = await f.StartAsync(f.Reader);
        await AnsweredAsync(f.Host, conversation, "Jak se připojím?", [Source(1, page)], Answer(("Přes VPN.", [1])));
        var own = await f.Host.As<AiConversationView>(f.Reader, new GetAiConversation(conversation.Id));
        Assert.Equal(("Síť", (string?)"sekce-1", (string?)null), (own.Messages[1].Sources.Single().Title, own.Messages[1].Sources.Single().HeadingId,
            own.Messages[1].Sources.Single().Excerpt));

        // Another person in the same browser, a WikiAdmin included: not found, never forbidden, and nothing in the list.
        foreach (var other in new[] { f.Writer, f.Admin })
        {
            await DeniedAsync(404, () => f.Host.As<AiConversationView>(other, new GetAiConversation(conversation.Id)));
            await DeniedAsync(404, () => f.Host.As<AiConversationView>(other, new ContinueAiConversation(conversation.Id)));
            await DeniedAsync(404, () => f.Host.As(other, new DeleteAiConversation(conversation.Id)));
            Assert.Empty((await f.Host.As<AiConversationList>(other, new ListAiConversations())).Items);
            await f.Host.As(other, new DeleteAllAiConversations());
        }
        Assert.Single((await f.Host.As<AiConversationList>(f.Reader, new ListAiConversations())).Items);

        // A revoked credential reads nothing: a rotated stamp or a deactivated account answers 401.
        await f.ExecuteAsync("""UPDATE "UserAccount" SET "SecurityStamp"='rotated' WHERE "Id"=@id""", ("id", f.Reader.Id));
        await DeniedAsync(401, () => f.Host.As<AiConversationView>(f.Reader, new GetAiConversation(conversation.Id)));
        await DeniedAsync(401, () => f.Host.As<AiConversationList>(f.Reader, new ListAiConversations()));
        await DeniedAsync(401, () => f.Host.As(f.Reader, new DeleteAllAiConversations()));
        var adminConversation = await f.StartAsync(f.Admin);
        await f.ExecuteAsync("""UPDATE "UserAccount" SET "IsActive"=false WHERE "Id"=@id""", ("id", f.Admin.Id));
        await DeniedAsync(401, () => f.Host.As<AiConversationView>(f.Admin, new GetAiConversation(adminConversation.Id)));
        await DeniedAsync(401, () => f.Host.As<AiConversationList>(f.Admin, new ListAiConversations()));
        Assert.NotNull(await f.StoreAsync(store => store.GetOwnedAsync(conversation.Id, f.Reader.Id, default)));
    }

    [PostgreSqlFact]
    public async Task Search_answers_are_listed_only_after_continue()
    {
        await using var f = await IndexFixture.CreateAsync();
        var reader = await f.Host.SeedUserAsync("reader");
        await f.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = reader.Id, Role = (int)SpaceRole.Reader }));
        var page = await f.PublishNewAsync("Síť", "Připojení přes VPN.");
        var saved = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, Chat(Model, 120, null)));
        await f.As<AiSwitchView>(f.Admin, new SaveAiSwitch(new SaveAiSwitchRequest(true, null)));
        var current = (await f.Cache.GetAsync(default)).Profile(AiProfileKind.Chat)!;
        var conversation = await StartAsync(f.Host, reader, (current.RevisionId, ChatProfileCompatibility.Fingerprint(current)), AiConversationOrigin.Search);
        await AnsweredAsync(f.Host, conversation, "Jak se připojím?", [Source(1, (page.Id, await f.PublishedRevisionAsync(page.Id)))],
            Answer(("Přes VPN.", [1])));

        // The omnisearch's conversation is the owner's, but not in "Moje konverzace".
        Assert.Empty((await f.Host.As<AiConversationList>(reader, new ListAiConversations())).Items);
        var read = await f.Host.As<AiConversationView>(reader, new GetAiConversation(conversation.Id));
        Assert.Equal((true, true, false), (read.Available, read.CanContinue, read.Outdated));

        var continued = await f.Host.As<AiConversationView>(reader, new ContinueAiConversation(conversation.Id));
        Assert.Equal(read, continued, ViewComparer.Instance);
        Assert.Equal(continued, await f.Host.As<AiConversationView>(reader, new ContinueAiConversation(conversation.Id)), ViewComparer.Instance);
        var listed = Assert.Single((await f.Host.As<AiConversationList>(reader, new ListAiConversations())).Items);
        Assert.Equal((conversation.Id, "Jak se připojím?"), (listed.Id, listed.Title));
        // Continue reuses the stored answer: the chat model was never called.
        Assert.DoesNotContain(f.Fake.Calls, call => call.Kind == AiProfileKind.Chat);

        // R5: a compatible revision (another timeout) keeps the conversation going, another model leaves it read-only.
        saved = await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, Chat(Model, 60, saved.Version)));
        Assert.Equal((true, false), await ContinuationAsync());
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Chat, Chat("gemma-5", 60, saved.Version)));
        Assert.Equal((false, true), await ContinuationAsync());
        Assert.True((await f.Host.As<AiConversationView>(reader, new GetAiConversation(conversation.Id))).Available);

        async Task<(bool CanContinue, bool Outdated)> ContinuationAsync()
        {
            var view = await f.Host.As<AiConversationView>(reader, new GetAiConversation(conversation.Id));
            return (view.CanContinue, view.Outdated);
        }
    }

    [PostgreSqlFact]
    public async Task A_withheld_release_keeps_its_question_and_list_title_protected()
    {
        await using var f = await AnswerFlowFixture.CreateAsync(AnswerFlowTests.Meaning);
        const string question = "Jak obnovit TAJNOU-ZALOHU?";
        await f.PublishAsync("Tajný návod", "## Postup obnovy\nSpusťte nástroj.", null, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        var prepared = await f.StartAsync(f.Reader, question, origin: AiConversationOrigin.Assistant);
        Assert.Equal(AiMessageState.Preparing, prepared.State);
        var hold = f.Fake.Hold(AiProfileKind.Chat);
        var completion = f.CompleteAsync(f.Reader, prepared.MessageId);
        await hold.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        await f.ExecuteAsync("""
            INSERT INTO "SpaceAiSetting"("Id","SpaceId","Included","ChangedById","ActiveFrom")
            VALUES (gen_random_uuid(),@space,false,@admin,now())
            """, ("space", f.Index.SpaceId), ("admin", f.Admin.Id));
        hold.Release();
        Assert.Equal(AiMessageState.Withheld, (await completion).State);
        Assert.NotEmpty((await f.StoredAsync(prepared.MessageId)).Dependencies);

        var view = await f.Index.Host.As<AiConversationView>(f.Reader, new GetAiConversation(prepared.ConversationId));
        Assert.Equal((false, false), (view.Available, view.CanContinue));
        Assert.All(view.Messages, message =>
        {
            Assert.Null(message.Question);
            Assert.Null(message.Scope);
            Assert.Empty(message.Sentences);
            Assert.Empty(message.Sources);
        });
        var listed = Assert.Single((await f.Index.Host.As<AiConversationList>(f.Reader, new ListAiConversations())).Items);
        Assert.Equal((false, (string?)null), (listed.Available, listed.Title));
        Assert.DoesNotContain(question, JsonSerializer.Serialize(view, s_json) + JsonSerializer.Serialize(listed, s_json));
        await AnswerFlowFixture.FailsAsync(409, AiAnswerProblems.ConversationUnavailable,
            () => f.FollowAsync(f.Reader, prepared.ConversationId, "Další otázka"));

        await f.ExecuteAsync("""UPDATE "SpaceAiSetting" SET "Included"=true WHERE "SpaceId"=@space""", ("space", f.Index.SpaceId));
        var restored = await f.Index.Host.As<AiConversationView>(f.Reader, new GetAiConversation(prepared.ConversationId));
        Assert.True(restored.Available);
        Assert.Equal(question, restored.Messages[0].Question);
        Assert.Equal(AiMessageState.Withheld, restored.Messages[1].State);
        Assert.Empty(restored.Messages[1].Sentences);
        Assert.Empty(restored.Messages[1].Sources);
    }

    [PostgreSqlTheory]
    [InlineData(AiMessageState.Failed)]
    [InlineData(AiMessageState.Canceled)]
    public async Task An_unreleased_turn_still_protects_its_question_when_a_dependency_is_revoked(string state)
    {
        await using var f = await ConversationFixture.CreateAsync();
        var page = await f.PublishAsync("Soukromý návod");
        var conversation = await f.StartAsync(f.Reader);
        var (_, answer) = await f.TurnAsync(conversation, "Otázka z chráněného návodu", [Source(1, page)]);
        Assert.True(await f.StoreAsync(store => store.CompleteAsync(answer.Id,
            new AiAnswerCompletion(state, null, null, ConversationFixture.Now(), []), default)));
        await f.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=@id""", ("id", page.PageId));
        var view = await f.Host.As<AiConversationView>(f.Reader, new GetAiConversation(conversation.Id));
        Assert.False(view.Available);
        Assert.Null(view.Messages[0].Question);
        Assert.Equal(AiMessageState.Withheld, view.Messages[1].State);
        Assert.All(view.Messages, message => Assert.Empty(message.Sources));
        var listed = Assert.Single((await f.Host.As<AiConversationList>(f.Reader, new ListAiConversations())).Items);
        Assert.False(listed.Available);
        Assert.Null(listed.Title);
    }
    private static SaveAiProfileRequest Chat(string model, int timeoutSeconds, string? expectedVersion) => new(true, AiProvider.OpenAiCompatible,
        AiLocation.Company, "http://lmstudio.example:1234/v1", model, null, null, null, null, timeoutSeconds, 2, "none", null, true, false, false,
        null, null, null, false, expectedVersion);

    /// <summary>A context dependency on the indexed chunk of the page's section in the active generation, as the preparation stores it.</summary>
    private static async Task<AiSourceDependencyRow> IndexedSourceAsync(AnswerSourcesFixture f, int ordinal, Guid pageId, string headingId)
    {
        await using var connection = new NpgsqlConnection(f.Index.Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT "RevisionId","Ordinal" FROM "KnowledgeChunk" WHERE "GenerationId"=@generation AND "PageId"=@page AND "HeadingId"=@heading
            """, connection);
        command.Parameters.AddWithValue("generation", f.Generation.Id);
        command.Parameters.AddWithValue("page", pageId);
        command.Parameters.AddWithValue("heading", headingId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return AiSourceDependencyRow.Page(ordinal, AiDependencyKind.Context, pageId, reader.GetGuid(0), f.Generation.Id, reader.GetInt16(1), headingId);
    }

    internal static async Task DeniedAsync(int status, Func<Task> action)
        => Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);

    /// <summary>Views compare by content: their lists are arrays.</summary>
    private sealed class ViewComparer : IEqualityComparer<AiConversationView>
    {
        public static readonly ViewComparer Instance = new();

        public bool Equals(AiConversationView? x, AiConversationView? y)
            => JsonSerializer.Serialize(x, s_json) == JsonSerializer.Serialize(y, s_json);

        public int GetHashCode(AiConversationView obj) => obj.Id.GetHashCode();
    }
}
