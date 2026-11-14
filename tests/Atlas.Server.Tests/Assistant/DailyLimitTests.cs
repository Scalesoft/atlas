using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Commands;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;
using Npgsql;

namespace Atlas.Server.Tests.Assistant;

/// <summary>The admin's daily limit per user of the answer tasks (plan phase 3 Q3, O10): none by default; over it the preparation
/// answers 429 <c>ai_limit</c> before anything is written. It counts the owner's answers of the task today, except no_sources.</summary>
public sealed class DailyLimitTests
{
    [PostgreSqlFact]
    public async Task The_daily_limit_counts_answers_of_the_owner_and_answers_429()
    {
        await using var f = await AnswerFlowFixture.CreateAsync(AnswerFlowTests.Meaning);
        await f.PublishAsync("Návod pro správce", "## Postup obnovy\nSpusťte nástroj.", null, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        f.Fake.ChatAnswer = FakeAi.Answers.Citing(1);
        async Task<AiFeaturesView> Save(params SaveAiFeatureItem[] items)
        {
            var current = await f.Index.As<AiFeaturesView>(f.Admin, new GetAiFeatures());
            return await f.Index.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new(items, current.Version)));
        }

        // No limit by default: answers and answers without sources alike.
        var initial = await f.Index.As<AiFeaturesView>(f.Admin, new GetAiFeatures());
        Assert.Equal((true, (int?)null), initial.Items.Where(item => item.Task == AiTaskCatalog.SearchAnswer).Select(item => (item.Limitable, item.DailyLimit)).Single());
        Assert.False(initial.Items.Single(item => item.Task == AiTaskCatalog.SearchEmbedQuery).Limitable);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(AiMessageState.Answered, (await f.AskAsync(f.Reader, "Jak obnovit data?")).Answer.State);
            Assert.Equal(AiMessageState.NoSources, (await f.StartAsync(f.Reader, "Kdo vyhrál turnaj v curlingu?")).State);
        }

        // Only limitable tasks, 1 to 10 000.
        await AnswerFlowFixture.FailsAsync(400, null, () => Save(new SaveAiFeatureItem(AiTaskCatalog.SearchEmbedQuery, true, null, 5)));
        await AnswerFlowFixture.FailsAsync(400, null, () => Save(new SaveAiFeatureItem(AiTaskCatalog.SearchAnswer, true, null, 0)));
        await AnswerFlowFixture.FailsAsync(400, null, () => Save(new SaveAiFeatureItem(AiTaskCatalog.SearchAnswer, true, null, 10_001)));
        var limited = await Save(new SaveAiFeatureItem(AiTaskCatalog.SearchAnswer, true, null, 3));
        Assert.Equal(3, limited.Items.Single(item => item.Task == AiTaskCatalog.SearchAnswer).DailyLimit);
        Assert.Null(limited.Items.Single(item => item.Task == AiTaskCatalog.ChatAnswer).DailyLimit);

        // Two answers so far (the two without sources do not count): one more, then 429 and nothing written.
        Assert.Equal(AiMessageState.Answered, (await f.AskAsync(f.Reader, "Jak obnovit data?")).Answer.State);
        var conversations = await f.CountAsync("""SELECT count(*) FROM "AiConversation" """);
        var refused = await AnswerFlowFixture.FailsAsync(429, AiAnswerProblems.Limit, () => f.StartAsync(f.Reader, "Jak obnovit data?"));
        Assert.Equal("Limit využití asistenta byl pro dnešek dosažen.", refused.Message);
        Assert.Equal(conversations, await f.CountAsync("""SELECT count(*) FROM "AiConversation" """));

        // Per owner and per task: another person and the assistant still answer.
        Assert.Equal(AiMessageState.Answered, (await f.AskAsync(f.Admin, "Jak obnovit data?")).Answer.State);
        Assert.Equal(AiMessageState.Answered, (await f.AskAsync(f.Reader, "Jak obnovit data?", origin: AiConversationOrigin.Assistant)).Answer.State);

        // Raised (or removed) by the admin, it applies at once.
        await Save(new SaveAiFeatureItem(AiTaskCatalog.SearchAnswer, true, null, null));
        Assert.Equal(AiMessageState.Answered, (await f.AskAsync(f.Reader, "Jak obnovit data?")).Answer.State);
    }

    [PostgreSqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleting_history_does_not_refund_the_daily_limit(bool all)
    {
        await using var f = await AnswerFlowFixture.CreateAsync(AnswerFlowTests.Meaning);
        await f.PublishAsync("Návod pro správce", "## Postup obnovy\nSpusťte nástroj.", null, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        f.Fake.ChatAnswer = FakeAi.Answers.Citing(1);
        var current = await f.Index.As<AiFeaturesView>(f.Admin, new GetAiFeatures());
        await f.Index.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new(
            [new SaveAiFeatureItem(AiTaskCatalog.SearchAnswer, true, null, 1)], current.Version)));
        var turn = await f.AskAsync(f.Reader, "Jak obnovit data?");
        Assert.Equal(AiMessageState.Answered, turn.Answer.State);
        await AnswerFlowFixture.FailsAsync(429, AiAnswerProblems.Limit, () => f.StartAsync(f.Reader, "Jak obnovit data?"));
        if (all) await f.Index.Host.As(f.Reader, new DeleteAllAiConversations());
        else await f.Index.Host.As(f.Reader, new DeleteAiConversation(turn.Answer.ConversationId));
        await f.Index.Host.As(f.Reader, new DeleteAllAiConversations());
        Assert.Equal(0, await f.CountAsync("""SELECT count(*) FROM "AiMessage" """));
        await AnswerFlowFixture.FailsAsync(429, AiAnswerProblems.Limit, () => f.StartAsync(f.Reader, "Jak obnovit data?"));
    }

    [PostgreSqlFact]
    public async Task A_reservation_waiting_across_UTC_midnight_is_counted_on_its_new_day()
    {
        await using var f = await AnswerFlowFixture.CreateAsync(AnswerFlowTests.Meaning);
        await f.PublishAsync("Návod pro správce", "## Postup obnovy\nSpusťte nástroj.", null, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        var current = await f.Index.As<AiFeaturesView>(f.Admin, new GetAiFeatures());
        await f.Index.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new(
            [new SaveAiFeatureItem(AiTaskCatalog.SearchAnswer, true, null, 1)], current.Version)));
        var midnight = f.Index.Clock.GetUtcNow().UtcDateTime.Date.AddDays(1);
        f.Index.Clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("UTC+2-test", TimeSpan.FromHours(2), "test", "test"));
        f.Index.Clock.SetUtcNow(new DateTimeOffset(midnight.AddSeconds(-1)));
        await f.Index.ProbeAsync();
        await using var connection = new NpgsqlConnection(f.Index.Host.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 812))", connection, transaction);
        hold.Parameters.AddWithValue("key", $"ai-limit:{f.Reader.Id:N}:{AiTaskCatalog.SearchAnswer}");
        await hold.ExecuteNonQueryAsync();
        var preparing = f.StartAsync(f.Reader, "Jak obnovit data?");
        try
        {
            await ConversationQuotaTests.WaitForLockAsync(f.Index.Host.ConnectionString);
            f.Index.Clock.Advance(TimeSpan.FromSeconds(2));
        }
        finally { await transaction.CommitAsync(); }
        var turn = await preparing;
        using var session = f.Index.Host.OpenSession();
        var created = await session.CreateSQLQuery("""SELECT "CreatedAt" FROM "AiMessage" WHERE "Id"=:id""")
            .AddScalar("CreatedAt", NHibernate.NHibernateUtil.UtcDateTime).SetParameter("id", turn.MessageId).UniqueResultAsync<DateTime>();
        Assert.Equal(midnight.AddSeconds(1), created);
        var usage = await f.Index.As<Atlas.Server.Features.Ai.Access.MyAiPolicyView>(f.Reader, new Atlas.Server.Features.Ai.Access.GetMyAiPolicy());
        Assert.Equal(midnight.AddDays(1), usage.Limits.Single(x => x.Task == AiTaskCatalog.SearchAnswer).ResetAt);
        await AnswerFlowFixture.FailsAsync(429, AiAnswerProblems.Limit, () => f.StartAsync(f.Reader, "Jak obnovit data?"));
    }

    [PostgreSqlFact]
    public async Task Concurrent_preparations_cannot_exceed_the_daily_limit()
    {
        await using var f = await AnswerFlowFixture.CreateAsync(AnswerFlowTests.Meaning);
        await f.PublishAsync("Návod pro správce", "## Postup obnovy\nSpusťte nástroj.", null, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        var current = await f.Index.As<AiFeaturesView>(f.Admin, new GetAiFeatures());
        await f.Index.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new(
            [new SaveAiFeatureItem(AiTaskCatalog.SearchAnswer, true, null, 1)], current.Version)));

        // Hold the first insert after its count. The second request must count only after that preparation commits.
        await using var connection = new NpgsqlConnection(f.Index.Host.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var tableLock = new NpgsqlCommand("""LOCK TABLE "AiConversation" IN SHARE MODE""", connection, transaction);
        await tableLock.ExecuteNonQueryAsync();
        var first = Prepare();
        await WaitForWaitingBackends(1);
        var second = Prepare();
        try
        {
            await WaitForWaitingBackends(2);
        }
        finally
        {
            await transaction.CommitAsync();
        }
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, error => error is null);
        var refused = Assert.Single(results, error => error is not null)!;
        Assert.Equal((429, AiAnswerProblems.Limit), (refused.Status, refused.Code));
        Assert.Equal(1, await f.CountAsync("""SELECT count(*) FROM "AiConversation" """));

        async Task<ApiException?> Prepare()
        {
            try
            {
                await f.StartAsync(f.Reader, "Jak obnovit data?");
                return null;
            }
            catch (ApiException error)
            {
                return error;
            }
        }

        async Task WaitForWaitingBackends(int count)
        {
            await using var monitor = new NpgsqlConnection(f.Index.Host.ConnectionString);
            await monitor.OpenAsync();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                await using var query = new NpgsqlCommand("""
                    SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock'
                    """, monitor);
                if (Convert.ToInt64(await query.ExecuteScalarAsync()) >= count)
                {
                    return;
                }
                Assert.True(DateTime.UtcNow < deadline, "The concurrent preparations did not reach the held insert.");
                await Task.Yield();
            }
        }
    }
}
