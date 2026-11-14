using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Prompts;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>Follow-up questions (plan phase 3 R4, R5, R6, O18): the history window, its transitive dependencies, one turn at a
/// time and the pinned chat profile.</summary>
public sealed class FollowUpTests
{
    /// <summary>Two concepts: restoring ("obnov…") and checking sums ("součt…"); a question finds the pages of its own only.</summary>
    private static string Meaning(string text)
        => text.Contains("obnov", StringComparison.OrdinalIgnoreCase) ? "backup restore database"
            : text.Contains("součt", StringComparison.OrdinalIgnoreCase) ? "checksum verification compare"
            : text;

    [PostgreSqlFact]
    public async Task Follow_ups_reuse_validated_history_and_copy_its_dependencies()
    {
        await using var f = await AnswerFlowFixture.CreateAsync(Meaning);
        var restricted = await f.Index.Host.SeedSpaceAsync("Omezený", (f.Writer, SpaceRole.Writer), (f.Reader, SpaceRole.Reader));
        var first = await f.PublishAsync("Postup obnovy", "## Postup obnovy\nSpusťte nástroj.", restricted, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        f.Fake.ChatAnswer = FakeAi.Answers.Answerable(("Spusťte nástroj [1].", [1]));

        var (prepared, answer) = await f.AskAsync(f.Reader, "Jak obnovit data?", origin: AiConversationOrigin.Assistant);
        Assert.Equal(first, Assert.Single(answer.Sources).PageId);
        var second = await f.PublishAsync("Kontrola záloh", "## Kontrola součtů\nPorovnejte kontrolní součty.", null, ("Kontrola součtů", "kontrola"));
        await f.IndexAsync();

        // The follow-up repeats the validated sentences (without their numbers) and copies the answer's dependencies as history.
        var followUp = await f.FollowAsync(f.Reader, prepared.ConversationId, "Jak zkontrolovat součty?");
        Assert.Equal(prepared.ConversationId, followUp.ConversationId);
        var reply = await f.CompleteAsync(f.Reader, followUp.MessageId);
        Assert.Equal(AiMessageState.Answered, reply.State);
        var call = f.ChatCalls[^1];
        Assert.Equal(AiTaskCatalog.ChatAnswer, call.Task);
        Assert.Equal([AnswerPrompt.System, "Jak obnovit data?", "Spusťte nástroj."], call.Inputs.Take(3));
        Assert.StartsWith("Otázka: Jak zkontrolovat součty?", call.Inputs[3]);
        var (_, dependencies) = await f.StoredAsync(followUp.MessageId);
        var context = dependencies.Where(row => row.Kind == AiDependencyKind.Context).ToArray();
        Assert.Equal(Enumerable.Range(1, followUp.Sources.Count), context.Select(row => row.Ordinal));
        Assert.Contains(context, row => row.PageId == second);
        // The first answer's source was not retrieved again, yet the prompt repeats what it said: a history dependency.
        Assert.Equal(first, Assert.Single(dependencies, row => row.Kind == AiDependencyKind.History).PageId);

        // The window holds three exchanges: the fifth question no longer repeats the first.
        for (var turn = 3; turn <= 5; turn++)
        {
            var next = await f.FollowAsync(f.Reader, prepared.ConversationId, $"Otázka číslo {turn} ke kontrole součtů?");
            Assert.Equal(AiMessageState.Answered, (await f.CompleteAsync(f.Reader, next.MessageId)).State);
        }
        var window = f.ChatCalls[^1].Inputs;
        Assert.Equal(1 + 3 * 2 + 1, window.Count);
        Assert.DoesNotContain("Jak obnovit data?", window);
        Assert.Contains("Jak zkontrolovat součty?", window);

        // Losing the first answer's source makes the whole conversation unavailable for a further question (spec RAG §4).
        await f.ExecuteAsync("""UPDATE "SpaceGrant" SET "ActiveTo"=clock_timestamp() WHERE "SpaceId"=@space AND "UserId"=@user AND "ActiveTo" IS NULL""",
            ("space", restricted), ("user", f.Reader.Id));
        await AnswerFlowFixture.FailsAsync(409, AiAnswerProblems.ConversationUnavailable,
            () => f.FollowAsync(f.Reader, prepared.ConversationId, "A ještě něco k součtům?"));
        // Another person's conversation is not found.
        await AnswerFlowFixture.FailsAsync(404, null, () => f.FollowAsync(f.Admin, prepared.ConversationId, "Cizí otázka k obnově?"));
    }

    [PostgreSqlFact]
    public async Task Only_one_turn_runs_at_a_time_and_an_incompatible_profile_makes_the_conversation_read_only()
    {
        await using var f = await AnswerFlowFixture.CreateAsync(Meaning);
        await f.PublishAsync("Postup obnovy", "## Postup obnovy\nSpusťte nástroj.", null, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        f.Fake.ChatAnswer = FakeAi.Answers.Citing(1);

        var prepared = await f.StartAsync(f.Reader, "Jak obnovit data?", origin: AiConversationOrigin.Assistant);
        await AnswerFlowFixture.FailsAsync(409, AiAnswerProblems.TurnInProgress, () => f.FollowAsync(f.Reader, prepared.ConversationId, "A dál k obnově?"));
        await f.CompleteAsync(f.Reader, prepared.MessageId);
        await AnswerFlowFixture.FailsAsync(409, AiAnswerProblems.TurnFinished, () => f.CompleteAsync(f.Reader, prepared.MessageId));

        // A key rotation keeps provider, location, URL origin and model: the conversation silently moves to the new revision.
        await f.ChangeChatAsync(apiKey: "sk-rotated-local");
        var rotated = await f.FollowAsync(f.Reader, prepared.ConversationId, "A dál k obnově?");
        Assert.Equal(AiMessageState.Answered, (await f.CompleteAsync(f.Reader, rotated.MessageId)).State);

        // Another model makes it read-only (spec RAG §3: nothing moves silently to another model).
        await f.ChangeChatAsync(model: "qwen-3-32b");
        await AnswerFlowFixture.FailsAsync(409, AiAnswerProblems.ConversationOutdated,
            () => f.FollowAsync(f.Reader, prepared.ConversationId, "A ještě k obnově?"));
        // A turn prepared before such a change is not completed on the new model.
        var started = await f.StartAsync(f.Reader, "Jak obnovit data?", origin: AiConversationOrigin.Assistant);
        await f.ChangeChatAsync(model: "gemma-4-26b-a4b");
        var outdated = await f.CompleteAsync(f.Reader, started.MessageId);
        Assert.Equal((AiMessageState.Failed, AiAnswerProblems.ConversationOutdated), (outdated.State, outdated.ErrorCode));
        Assert.Equal(2, f.ChatCalls.Count);
        // A new conversation starts on the current profile.
        Assert.Equal(AiMessageState.Answered, (await f.AskAsync(f.Reader, "Jak obnovit data?")).Answer.State);
        Assert.Equal(AiProfileKind.Chat, f.ChatCalls[^1].Kind);
    }
}
