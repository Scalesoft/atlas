using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>COL2-6: the pending log (updates above the snapshot) is counted with the head instead of re-read on every append.</summary>
public sealed class CollaborationPendingTotalsTests
{
    [PostgreSqlFact]
    public async Task An_append_is_bounded_by_the_running_totals_not_by_reading_the_log()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        await f.Append(opened.DocumentId, Guid.NewGuid(), [1, 2, 3]);
        await f.Append(opened.DocumentId, Guid.NewGuid(), [4, 5]);
        Assert.Equal((2, 5L), await Totals(f));
        // A retry of a stored update counts nothing.
        var retried = Guid.NewGuid();
        await f.Append(opened.DocumentId, retried, [6]);
        Assert.True((await f.Append(opened.DocumentId, retried, [6])).Duplicate);
        Assert.Equal((3, 6L), await Totals(f));

        // A full window refuses the next update from the counter alone, a few rows are no full window and thousands of
        // rows the counter does not know about are not read: the append costs the same with any log.
        await SetTotals(f, CollaborationLog.RecentCapacity, 6);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Append(opened.DocumentId, Guid.NewGuid(), [7]));
        await SetTotals(f, 3, CollaborationLog.RecentPayloadBytes - 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Append(opened.DocumentId, Guid.NewGuid(), [7, 8]));
        await SetTotals(f, 3, 6);
        Assert.Equal(4, (await f.Append(opened.DocumentId, Guid.NewGuid(), [7])).Sequence);

        // A stored snapshot recounts what stays above it; a reset empties the totals with the log.
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(2), default);
        Assert.Equal((2, 2L), await Totals(f));
        using (var session = f.Database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            var page = await session.GetAsync<Page>(f.Page.Id, NHibernate.LockMode.Upgrade);
            var revision = await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync();
            await f.Store.ResetAsync(session, page, revision, default);
            await tx.CommitAsync();
        }
        Assert.Equal((0, 0L), await Totals(f));
    }

    /// <summary>Migration 2026103035 over live pages an installation already has: each starts from the updates above its snapshot.</summary>
    [PostgreSqlFact]
    public async Task The_migration_counts_the_pending_log_of_every_existing_live_page()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_collab_pending_totals_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026103035);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','admin','Admin',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','Space','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version","SortOrder") VALUES
              ('00000000-0000-0000-0000-0000000000a1','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1,1024),
              ('00000000-0000-0000-0000-0000000000a2','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1,2048);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-0000000000a1',1,'A','{"type":"doc","content":[]}','','00000000-0000-0000-0000-000000000001',now(),'web'),
              ('00000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-0000000000a2',1,'B','{"type":"doc","content":[]}','','00000000-0000-0000-0000-000000000001',now(),'web');
            INSERT INTO "PageCollaboration"("PageId","Generation","BaseRevisionId","Sequence","SnapshotSequence") VALUES
              ('00000000-0000-0000-0000-0000000000a1','00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000b1',5,2),
              ('00000000-0000-0000-0000-0000000000a2','00000000-0000-0000-0000-0000000000c2','00000000-0000-0000-0000-0000000000b2',0,0);
            INSERT INTO "CollaborationUpdate"("PageId","Generation","Sequence","ClientUpdateId","UserId","ReceivedAt","Payload")
            SELECT '00000000-0000-0000-0000-0000000000a1','00000000-0000-0000-0000-0000000000c1',n,gen_random_uuid(),
              '00000000-0000-0000-0000-000000000001',now(),decode(repeat('01',n),'hex')
            FROM generate_series(1,5) n;
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        // Sequences 3, 4 and 5 are above the snapshot: 3 updates of 3 + 4 + 5 bytes.
        Assert.Equal("a1:3:12,a2:0:0", await ScalarAsync(schema.ConnectionString, """
            SELECT string_agg(right(CAST("PageId" AS text), 2) || ':' || "PendingCount" || ':' || "PendingBytes", ',' ORDER BY "PageId")
            FROM "PageCollaboration"
            """));
    }

    private static async Task<(int Count, long Bytes)> Totals(CollaborationFixture f)
    {
        using var session = f.Host.OpenSession();
        var state = await session.GetAsync<PageCollaboration>(f.Page.Id);
        return (state.PendingCount, state.PendingBytes);
    }

    private static async Task SetTotals(CollaborationFixture f, int count, long bytes)
    {
        using var session = f.Host.OpenSession();
        using var tx = session.BeginTransaction();
        await session.CreateSQLQuery("""UPDATE "PageCollaboration" SET "PendingCount" = :count, "PendingBytes" = :bytes WHERE "PageId" = :page""")
            .SetParameter("count", count).SetParameter("bytes", bytes).SetParameter("page", f.Page.Id).ExecuteUpdateAsync();
        await tx.CommitAsync();
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static Task ExecuteAsync(string connectionString, string sql) => ScalarAsync(connectionString, sql);
}
