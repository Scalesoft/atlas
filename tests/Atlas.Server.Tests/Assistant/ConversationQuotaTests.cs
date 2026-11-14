using Atlas.Server.Features.Assistant;
using Atlas.Server.Features.Assistant.Commands;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Persistence;
using NHibernate;
using Npgsql;
using Xunit;
using static Atlas.Server.Tests.Assistant.ConversationSeeds;

namespace Atlas.Server.Tests.Assistant;

public sealed class ConversationQuotaTests
{
    [PostgreSqlFact]
    public async Task Deletion_rolls_back_atomically_and_keeps_each_days_consumption()
    {
        await using var f = await ConversationFixture.CreateAsync();
        var day = ConversationFixture.Now().Date;
        var old = await f.StartAsync(f.Reader, at: day.AddDays(-1));
        await AnsweredAsync(f.Host, old, "Yesterday", [], Answer(("Answer", [])), day.AddHours(-1));
        var current = await f.StartAsync(f.Reader, at: day);
        await AnsweredAsync(f.Host, current, "Today", [], Answer(("Answer", [])), day.AddHours(1));
        await f.TurnAsync(current, "No sources", [], day.AddHours(2));
        using (var session = f.Host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var store = new ConversationStore(session);
            Assert.True(await store.DeleteOwnedAsync(current.Id, f.Reader.Id, default));
            Assert.Equal(1, await store.DailyCountAsync(f.Reader.Id, "chat.answer", day, day.AddDays(1), default));
            await transaction.RollbackAsync();
        }
        Assert.Equal(4, await ConversationDeleteTests.CountAsync(f, "AiMessage", "ConversationId", current.Id));
        await f.Host.As(f.Reader, new DeleteAllAiConversations());
        Assert.Equal(1, await f.StoreAsync(s => s.DailyCountAsync(f.Reader.Id, "chat.answer", day, day.AddDays(1), default)));
        // Each UTC day of the deleted answers keeps its count; trimming the past days is OperationalRetentionTests' (a sweep over the
        // whole table, which a test on the shared host does not run).
        using var read = f.Host.OpenSession();
        var days = await read.CreateSQLQuery("""SELECT "Day" FROM "AiDeletedDailyUsage" WHERE "OwnerId" = :owner ORDER BY "Day" """)
            .AddScalar("Day", NHibernateUtil.UtcDateTime).SetGuid("owner", f.Reader.Id).ListAsync<DateTime>();
        Assert.Equal([day.AddDays(-1), day], days);
        Assert.Equal(0, await f.StoreAsync(s => s.DailyCountAsync(f.Reader.Id, "chat.answer", day.AddDays(1), day.AddDays(2), default)));
    }

    [PostgreSqlFact]
    public async Task Deletion_waits_for_no_sources_completion_before_preserving_consumption()
    {
        await using var f = await ConversationFixture.CreateAsync();
        var page = await f.PublishAsync("Guide");
        var conversation = await f.StartAsync(f.Reader);
        var (_, answer) = await f.TurnAsync(conversation, "Question", [Source(1, page)]);
        using var session = f.Host.OpenSession();
        using var transaction = session.BeginTransaction();
        var store = new ConversationStore(session);
        Assert.True(await store.CompleteAsync(answer.Id, new AiAnswerCompletion(AiMessageState.NoSources, null, null,
            ConversationFixture.Now(), []), default));
        // The shared database has other tests' lock waits: wait for the one behind this transaction.
        var holder = await session.CreateSQLQuery("SELECT pg_backend_pid()").UniqueResultAsync<int>();
        var deleting = f.Host.As(f.Reader, new DeleteAiConversation(conversation.Id));
        try { await WaitForLockAsync(f.Host.ConnectionString, holder); }
        finally { await transaction.CommitAsync(); }
        await deleting.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, await f.StoreAsync(s => s.DailyCountAsync(f.Reader.Id, "chat.answer",
            answer.CreatedAt.Date, answer.CreatedAt.Date.AddDays(1), default)));
        Assert.Equal(0, await ConversationDeleteTests.CountAsync(f, "AiMessage", "ConversationId", conversation.Id));
    }

    [PostgreSqlFact]
    public async Task Maintenance_skips_locked_messages_then_cleans_them_next_round()
    {
        // Isolated: CancelStaleAiTurns cancels every stale turn of the whole AiMessage table and counts them.
        await using var f = await ConversationFixture.CreateAsync(isolated: true);
        var day = ConversationFixture.Now().Date;
        var page = await f.PublishAsync("Guide");
        var conversation = await f.StartAsync(f.Reader, at: day.AddDays(-1));
        var (_, answer) = await f.TurnAsync(conversation, "Question", [Source(1, page)], day.AddHours(-1));
        await using var connection = new NpgsqlConnection(f.Host.ConnectionString);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var hold = new NpgsqlCommand("""SELECT "Id" FROM "AiMessage" WHERE "Id"=@id FOR UPDATE""", connection, transaction);
            hold.Parameters.AddWithValue("id", answer.Id);
            await hold.ExecuteScalarAsync();
            try
            {
                var canceled = await f.Host.InvokeAsync<CanceledAiTurns>(new CancelStaleAiTurns(day, day.AddHours(1)))
                    .WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(0, canceled.Count);
            }
            finally { await transaction.RollbackAsync(); }
        }
        Assert.Equal(1, (await f.Host.InvokeAsync<CanceledAiTurns>(new CancelStaleAiTurns(day, day.AddHours(1)))).Count);
    }

    /// <param name="holder">The backend holding the lock: only a wait behind it counts (null: any lock wait in the database).</param>
    internal static async Task WaitForLockAsync(string connectionString, int? holder = null)
    {
        await using var monitor = new NpgsqlConnection(connectionString);
        await monitor.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var query = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock'
                  AND (@holder IS NULL OR @holder = ANY(pg_blocking_pids(pid)))
                """, monitor);
            query.Parameters.Add(new NpgsqlParameter("holder", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)holder ?? DBNull.Value });
            if (Convert.ToInt64(await query.ExecuteScalarAsync()) > 0) return;
            Assert.True(DateTime.UtcNow < deadline, "The operation did not reach the held database lock.");
            await Task.Yield();
        }
    }
}
