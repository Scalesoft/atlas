using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Retention;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Events;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Platform;

/// <summary>The one cleanup of the operational logs (PER1-1, docs/architecture.md §5.2): age, count, batch bounds, the stop of an
/// optional log, and the migration that trimmed the board sync log.</summary>
/// <remarks>Own hosts (BoardsFixture.CreateAsync): the tests change the host-wide RetentionOptions and run the retention over whole
/// tables, which would delete other tests' rows on a shared host.</remarks>
public sealed class OperationalRetentionTests
{
    [PostgreSqlFact]
    public async Task Rows_past_the_age_go_and_younger_ones_stay()
    {
        await using var f = await BoardsFixture.CreateAsync();
        var board = await f.CreateBoardAsync();
        for (var i = 0; i < 3; i++)
        {
            await f.ApplyAsync(f.Writer, board, BoardsFixture.Note(Guid.NewGuid()));
        }
        await Sql(f, $"""
            UPDATE "BoardOperation" SET "AppliedAt" = now() - interval '31 days' WHERE "BoardId" = '{board}' AND "Sequence" = 1;
            UPDATE "BoardOperation" SET "AppliedAt" = now() - interval '29 days' WHERE "BoardId" = '{board}' AND "Sequence" = 2;
            {Page(f)}
            INSERT INTO "ProjectorCatalogEvent"("PageId","ActorId","IsCatalog","Channel","OccurredAt") VALUES
              ('{PageId}', '{f.Admin.Id}', true, 'web', now() - interval '1826 days'),
              ('{PageId}', '{f.Admin.Id}', false, 'web', now() - interval '1824 days');
            """);

        var deleted = await Retention(f).RunAsync(default);

        Assert.Equal(1, deleted["BoardOperation"]);
        Assert.Equal("2,3", await Sql(f, $"""SELECT string_agg("Sequence"::text, ',' ORDER BY "Sequence") FROM "BoardOperation" WHERE "BoardId" = '{board}'"""));
        Assert.Equal(1, deleted["ProjectorCatalogEvent"]);
        Assert.Equal(1L, await Sql(f, """SELECT count(*) FROM "ProjectorCatalogEvent" """));
    }

    [PostgreSqlFact]
    public async Task A_board_keeps_its_newest_batches_up_to_the_count_in_the_writer_and_in_the_job()
    {
        await using var f = await BoardsFixture.CreateAsync();
        var (busy, quiet) = (await f.CreateBoardAsync(), await f.CreateBoardAsync());
        for (var i = 0; i < 5; i++)
        {
            await f.ApplyAsync(f.Writer, busy, BoardsFixture.Note(Guid.NewGuid()));
        }
        await f.ApplyAsync(f.Writer, quiet, BoardsFixture.Note(Guid.NewGuid()));
        var options = f.Host.Services.GetRequiredService<RetentionOptions>();
        options.Logs["BoardOperation"] = new RetentionLimit { MaxRows = 2 };

        // A board nobody writes to any more: the job trims it.
        Assert.Equal(3, (await Retention(f).RunAsync(default))["BoardOperation"]);
        Assert.Equal("4,5", await Sequences(f, busy));
        Assert.Equal("1", await Sequences(f, quiet));
        // The next batch: the writer keeps the count itself, and a client further behind than it is told to reload.
        await f.ApplyAsync(f.Writer, busy, BoardsFixture.Note(Guid.NewGuid()));
        Assert.Equal("5,6", await Sequences(f, busy));
        Assert.Equal(6, (await f.As<BoardOperationsDto>(f.Reader, new Atlas.Server.Features.Boards.Queries.GetBoardOperations(busy, 4))).Sequence);
        await BoardsFixture.Refused(409, () => f.As<BoardOperationsDto>(f.Reader, new Atlas.Server.Features.Boards.Queries.GetBoardOperations(busy, 3)), "resync_required");
    }

    [PostgreSqlFact]
    public async Task The_logs_once_trimmed_by_their_writers_age_and_count_in_the_job_over_existing_rows()
    {
        await using var f = await BoardsFixture.CreateAsync();
        var board = await f.CreateBoardAsync();
        var other = await f.CreateBoardAsync();
        // Rows as 3.0 finds them: page views of any age, counters past their age, and accounts past their count.
        await Sql(f, $"""
            {Page(f)}
            INSERT INTO "PageView"("Id","PageId","UserId","ViewedAt") VALUES
              (gen_random_uuid(), '{PageId}', '{f.Writer.Id}', now() - interval '401 days'),
              (gen_random_uuid(), '{PageId}', '{f.Writer.Id}', now() - interval '399 days');
            INSERT INTO "AiInteractionDaily"("Day","Task","Kind","Outcome","Count") VALUES
              (current_date - 91, 'editor.transform', 'shorten', 'applied', 1), (current_date - 89, 'editor.transform', 'shorten', 'applied', 1);
            INSERT INTO "AiDeletedDailyUsage"("OwnerId","Task","Day","Count") VALUES
              ('{f.Writer.Id}', 'chat.answer', date_trunc('day', now() - interval '1 day', 'UTC'), 1),
              ('{f.Writer.Id}', 'chat.answer', date_trunc('day', now(), 'UTC'), 1);
            INSERT INTO "UserSignIn"("Id","UserId","AuthenticatedAt","AuthenticationMethod")
              SELECT gen_random_uuid(), '{f.Writer.Id}', now() - make_interval(days => n), 'builtin' FROM generate_series(1, 102) n;
            INSERT INTO "UserSignIn"("Id","UserId","AuthenticatedAt","AuthenticationMethod") VALUES (gen_random_uuid(), '{f.Reader.Id}', now() - interval '3000 days', 'builtin');
            INSERT INTO "PageVisit"("Id","UserId","PageId","LastVisitedAt") VALUES
              (gen_random_uuid(), '{f.Writer.Id}', '{board}', now() - interval '2 days'), (gen_random_uuid(), '{f.Writer.Id}', '{other}', now()),
              (gen_random_uuid(), '{f.Reader.Id}', '{board}', now() - interval '3000 days');
            """);
        var options = f.Host.Services.GetRequiredService<RetentionOptions>();
        options.Logs["PageVisit"] = new RetentionLimit { MaxRows = 1 };

        var deleted = await Retention(f).RunAsync(default);

        Assert.Equal((1, 1, 1, 2, 1), (deleted["PageView"], deleted["AiInteractionDaily"], deleted["AiDeletedDailyUsage"], deleted["UserSignIn"], deleted["PageVisit"]));
        Assert.Equal(1L, await Sql(f, """SELECT count(*) FROM "PageView" WHERE "ViewedAt" > now() - interval '400 days'"""));
        // A count without an age: the newest per account stay, also a dormant account's last sign-in and visit.
        Assert.Equal(1L, await Sql(f, $"""SELECT count(*) FROM "UserSignIn" WHERE "UserId"='{f.Reader.Id}'"""));
        Assert.Equal(100L, await Sql(f, $"""SELECT count(*) FROM "UserSignIn" WHERE "UserId"='{f.Writer.Id}' AND "AuthenticatedAt" > now() - interval '101 days'"""));
        Assert.Equal($"{other}", (await Sql(f, $"""SELECT "PageId"::text FROM "PageVisit" WHERE "UserId"='{f.Writer.Id}'""")));
        Assert.Equal(1L, await Sql(f, $"""SELECT count(*) FROM "PageVisit" WHERE "UserId"='{f.Reader.Id}'"""));
        Assert.Equal(1L, await Sql(f, """SELECT count(*) FROM "AiDeletedDailyUsage" """));
    }

    [PostgreSqlFact]
    public async Task A_run_deletes_at_most_its_batches_and_the_next_run_continues()
    {
        await using var f = await BoardsFixture.CreateAsync();
        await Sql(f, $"""
            {Page(f)}
            INSERT INTO "ProjectorCatalogEvent"("PageId","ActorId","IsCatalog","Channel","OccurredAt")
            SELECT '{PageId}', '{f.Admin.Id}', true, 'web', now() - interval '2000 days' FROM generate_series(1, 5)
            """);
        var options = f.Host.Services.GetRequiredService<RetentionOptions>();
        (options.BatchSize, options.MaxBatchesPerRun) = (2, 1);

        Assert.Equal(2, (await Retention(f).RunAsync(default))["ProjectorCatalogEvent"]);
        Assert.Equal(3L, await Sql(f, """SELECT count(*) FROM "ProjectorCatalogEvent" """));
        Assert.Equal(2, (await Retention(f).RunAsync(default))["ProjectorCatalogEvent"]);
        Assert.Equal(1, (await Retention(f).RunAsync(default))["ProjectorCatalogEvent"]);
    }

    /// <summary>The runs of the jobs (3.3 §4.5): the newest 20 finished runs of each job and 30 days; a queued or running run stays however old.</summary>
    [PostgreSqlFact]
    public async Task Finished_job_runs_keep_the_newest_twenty_of_each_job_and_thirty_days_and_unfinished_ones_stay()
    {
        await using var f = await BoardsFixture.CreateAsync();
        await Sql(f, """
            INSERT INTO "JobRun"("JobId","Trigger","State","FinishedAt")
              SELECT 'busy', 'schedule', 'succeeded', now() - make_interval(mins => n) FROM generate_series(1, 25) n;
            INSERT INTO "JobRun"("JobId","Trigger","State","FinishedAt") VALUES
              ('quiet', 'schedule', 'failed', now() - interval '31 days'),
              ('quiet', 'manual', 'cancelled', now() - interval '29 days');
            INSERT INTO "JobRun"("JobId","Trigger","State","StartedAt") VALUES ('busy', 'manual', 'running', now() - interval '40 days');
            INSERT INTO "JobRun"("JobId","Key","Trigger","State","QueuedAt") VALUES ('busy', 'k', 'event', 'queued', now() - interval '40 days');
            """);

        var deleted = await Retention(f).RunAsync(default);

        Assert.Equal(6, deleted["JobRun"]);
        Assert.Equal(20L, await Sql(f, """SELECT count(*) FROM "JobRun" WHERE "JobId" = 'busy' AND "State" = 'succeeded' AND "FinishedAt" > now() - interval '21 minutes' """));
        Assert.Equal(20L, await Sql(f, """SELECT count(*) FROM "JobRun" WHERE "JobId" = 'busy' AND "State" = 'succeeded' """));
        Assert.Equal("cancelled", await Sql(f, """SELECT string_agg("State", ',') FROM "JobRun" WHERE "JobId" = 'quiet' """));
        Assert.Equal(2L, await Sql(f, """SELECT count(*) FROM "JobRun" WHERE "State" IN ('running', 'queued') """));
    }

    [PostgreSqlFact]
    public async Task The_retention_job_runs_to_the_end_in_the_calling_flow()
    {
        await using var f = await BoardsFixture.CreateAsync();
        await Sql(f, $"""
            {Page(f)}
            INSERT INTO "ProjectorCatalogEvent"("PageId","ActorId","IsCatalog","Channel","OccurredAt")
            SELECT '{PageId}', '{f.Admin.Id}', true, 'web', now() - interval '2000 days' FROM generate_series(1, 2)
            """);

        var runId = await f.Host.Services.GetRequiredService<IJobRunner>().RunToEndAsync("retention", "test");

        Assert.Equal("succeeded", await Sql(f, $"""SELECT "State" FROM "JobRun" WHERE "Id" = {runId}"""));
        Assert.Equal(0L, await Sql(f, """SELECT count(*) FROM "ProjectorCatalogEvent" """));
    }

    /// <summary>An optional log at its count stops taking rows until the age makes room; an audit is never trimmed by count.</summary>
    [PostgreSqlFact]
    public async Task An_optional_log_at_its_count_stops_its_writers_and_an_audit_ignores_a_count()
    {
        await using var f = await BoardsFixture.CreateAsync();
        var options = f.Host.Services.GetRequiredService<RetentionOptions>();
        options.Logs["SearchQueryLog"] = new RetentionLimit { MaxRows = 2 };
        options.Logs["ProjectorCatalogEvent"] = new RetentionLimit { MaxRows = 1 };
        var gate = f.Host.Services.GetRequiredService<OptionalLogGate>();
        await Sql(f, $"""
            INSERT INTO "SearchQueryLog"("SpaceId","NormalizedQuery","DisplayQuery","UserKey","OccurredOn")
            SELECT '{f.SpaceId}', 'vpn', 'VPN', decode(md5(n::text), 'hex'), current_date - n FROM generate_series(1, 3) n;
            {Page(f)}
            INSERT INTO "ProjectorCatalogEvent"("PageId","ActorId","IsCatalog","Channel")
            SELECT '{PageId}', '{f.Admin.Id}', true, 'web' FROM generate_series(1, 3);
            """);

        await Retention(f).RunAsync(default);
        // The oldest went, two are left, so the log is full.
        Assert.Equal("1,2", await Sql(f, """SELECT string_agg((current_date - "OccurredOn")::text, ',' ORDER BY "OccurredOn" DESC) FROM "SearchQueryLog" """));
        Assert.False(gate.Accepts("SearchQueryLog"));
        Assert.True(gate.Accepts("AiUsage"));
        Assert.Equal(3L, await Sql(f, """SELECT count(*) FROM "ProjectorCatalogEvent" """));

        await Sql(f, """UPDATE "SearchQueryLog" SET "OccurredOn" = current_date - 91 WHERE "OccurredOn" = current_date - 2""");
        await Retention(f).RunAsync(default);
        Assert.True(gate.Accepts("SearchQueryLog"));
    }

    /// <summary>Migration 2026102995 over existing rows: each board keeps its last 500 batches, and the board revisions no consumer read
    /// leave the outbox while a page's stays.</summary>
    [PostgreSqlFact]
    public async Task The_migration_trims_the_board_sync_log_and_drops_the_unread_board_revisions()
    {
        const long Version = 2026102995;
        await using var schema = await DisposableSchema.CreateAsync("atlas_retention_upgrade_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, Version);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a','Busy',600,now(),'00000000-0000-0000-0000-000000000001',now(),false,1024),
              ('00000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-00000000000a','Quiet',3,now(),'00000000-0000-0000-0000-000000000001',now(),false,2048);
            INSERT INTO "BoardOperation"("BoardId","Sequence","OperationId","AuthorId","AppliedAt","ChangesJson")
              SELECT '00000000-0000-0000-0000-0000000000b1'::uuid, n, gen_random_uuid(), '00000000-0000-0000-0000-000000000001'::uuid, now(), '[]' FROM generate_series(1, 600) n
              UNION ALL
              SELECT '00000000-0000-0000-0000-0000000000b2'::uuid, n, gen_random_uuid(), '00000000-0000-0000-0000-000000000001'::uuid, now(), '[]' FROM generate_series(1, 3) n;
            INSERT INTO "OutboxMessage"("ModuleId","MessageType","Payload","OrderingKey") VALUES
              ('platform','Atlas.Server.Platform.Events.ContentRevised, Atlas.Server','{"resourceId":"00000000-0000-0000-0000-0000000000b1","contentType":"atlas.board","revisionKind":"working","revisionId":null}','00000000-0000-0000-0000-0000000000b1'),
              ('platform','Atlas.Server.Platform.Events.ContentRevised, Atlas.Server','{"resourceId":"00000000-0000-0000-0000-0000000000f1","contentType":"atlas.page","revisionKind":"working","revisionId":null}','00000000-0000-0000-0000-0000000000f1');
            """);

        // Up to 2026110421, which hands the queue to the upgrade job.
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110421, from: Version);

        Assert.Equal("00000000-0000-0000-0000-0000000000b1|500|101|600,00000000-0000-0000-0000-0000000000b2|3|1|3", await OutboxTests.ScalarAsync(schema.ConnectionString, """
            SELECT string_agg(b, ',' ORDER BY b) FROM (
              SELECT "BoardId"::text || '|' || count(*) || '|' || min("Sequence") || '|' || max("Sequence") AS b FROM "BoardOperation" GROUP BY "BoardId") x
            """));
        Assert.Equal("atlas.page", await OutboxTests.ScalarAsync(schema.ConnectionString, """SELECT string_agg("Payload"->>'contentType', ',') FROM "OutboxMessage" """));
    }

    private static readonly Guid PageId = Guid.Parse("00000000-0000-0000-0000-0000000000e1");

    /// <summary>A page the projector audit rows name (their key restricts its delete).</summary>
    private static string Page(BoardsFixture f) => $"""INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{PageId}','{f.SpaceId}','{f.Writer.Id}',1) ON CONFLICT DO NOTHING;""";

    private static OperationalRetention Retention(BoardsFixture f) => f.Host.Services.GetRequiredService<OperationalRetention>();

    private static Task<object?> Sql(BoardsFixture f, string sql) => OutboxTests.ScalarAsync(f.Host.ConnectionString, sql);

    private static async Task<string?> Sequences(BoardsFixture f, Guid board)
        => (string?)await Sql(f, $"""SELECT string_agg("Sequence"::text, ',' ORDER BY "Sequence") FROM "BoardOperation" WHERE "BoardId" = '{board}'""");
}
