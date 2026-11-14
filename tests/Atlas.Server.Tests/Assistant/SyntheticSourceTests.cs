using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Assistant.Answering;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Sources;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.Assistant.Fakes;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>AI-11 (spec RAG §1): a second source type, registered next to pages in the test host only, goes through the same
/// retrieval, prompt, citations, history and revocation gate; the common flow has no branch on a source type.</summary>
public sealed class SyntheticSourceTests
{
    [PostgreSqlFact]
    public async Task A_second_source_type_goes_through_retrieval_citations_follow_ups_and_revocation_unchanged()
    {
        var notes = new SyntheticNotes();
        await using var f = await AnswerFlowFixture.CreateAsync(AnswerFlowTests.Meaning, services =>
        {
            services.AddSingleton(notes);
            services.AddScoped<IKnowledgeSourceAdapter, SyntheticNoteAdapter>();
        });
        var page = await f.PublishAsync("Návod pro správce", "## Postup obnovy\nSpusťte nástroj.", null, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        notes.Put(new SyntheticNote("helpdesk", "Poznámka helpdesku", "POZNÁMKA-TEXT: kdo chce obnovit data, nahlásí to helpdesku.", 1,
            new HashSet<Guid> { f.Reader.Id }, 10));
        f.Fake.ChatAnswer = FakeAi.Answers.From(sources => new AnswerResult(true,
        [
            new("Nahlaste to helpdesku.", [sources.Single(source => source.Page == "Poznámka helpdesku").Number]),
            new("Pak spusťte nástroj.", [sources.Single(source => source.Page == "Návod pro správce").Number]),
        ]));

        // Retrieval merges both types by rank, the prompt carries both, both are cited and stored as dependencies of their kind.
        var (prepared, answer) = await f.AskAsync(f.Reader, "Jak obnovit data?", origin: AiConversationOrigin.Assistant);
        Assert.Equal(["note", "page"], prepared.Sources.Select(source => source.Type));
        Assert.Equal((null, "Poznámka helpdesku"), (prepared.Sources[0].PageId, prepared.Sources[0].Title));
        Assert.Equal(AiMessageState.Answered, answer.State);
        Assert.Equal([("note", true), ("page", true)], answer.Sources.Select(source => (source.Type, source.Cited)));
        Assert.Equal(page, answer.Sources[1].PageId);
        Assert.Contains("POZNÁMKA-TEXT", f.ChatCalls[^1].Inputs[^1]);
        var (_, dependencies) = await f.StoredAsync(answer.Id);
        Assert.Equal(("note", 1, null, "helpdesk", "1"),
            (dependencies[0].SourceType, dependencies[0].SourceVersion, dependencies[0].PageId, dependencies[0].ExternalKey, dependencies[0].ExternalRevision));

        // A follow-up keeps both as history.
        f.Fake.ChatAnswer = FakeAi.Answers.Citing(1);
        var followUp = await f.FollowAsync(f.Reader, prepared.ConversationId, "A co dál s obnovou?");
        Assert.Equal(AiMessageState.Answered, (await f.CompleteAsync(f.Reader, followUp.MessageId)).State);

        // A stored reference of a version no adapter serves is refused: withheld before the model.
        var unknown = await f.StartAsync(f.Reader, "Jak obnovit data?");
        await f.ExecuteAsync("""UPDATE "AiSourceDependency" SET "SourceVersion"=2 WHERE "MessageId"=@id AND "SourceType"='note'""", ("id", unknown.MessageId));
        var calls = f.ChatCalls.Count;
        Assert.Equal(AiMessageState.Withheld, (await f.CompleteAsync(f.Reader, unknown.MessageId)).State);
        Assert.Equal(calls, f.ChatCalls.Count);

        // The note revoked during the model call: the gate withholds the answer.
        var during = await f.StartAsync(f.Reader, "Jak obnovit data?");
        var hold = f.Fake.Hold(AiProfileKind.Chat);
        var completion = f.CompleteAsync(f.Reader, during.MessageId);
        await hold.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        notes.Revoke("helpdesk", f.Reader.Id);
        hold.Release();
        var withheld = await completion;
        Assert.Equal(AiMessageState.Withheld, withheld.State);
        Assert.Null((await f.StoredAsync(during.MessageId)).Message.Answer);

        // Afterwards the note never reaches the model, and the conversation that used it cannot go on.
        var (after, pageOnly) = await f.AskAsync(f.Reader, "Jak obnovit data?");
        Assert.Equal(["page"], after.Sources.Select(source => source.Type));
        Assert.Equal(AiMessageState.Answered, pageOnly.State);
        Assert.DoesNotContain("POZNÁMKA-TEXT", string.Join("\n", f.ChatCalls[^1].Inputs));
        await AnswerFlowFixture.FailsAsync(409, AiAnswerProblems.ConversationUnavailable,
            () => f.FollowAsync(f.Reader, prepared.ConversationId, "A ještě k obnově?"));
    }
}
