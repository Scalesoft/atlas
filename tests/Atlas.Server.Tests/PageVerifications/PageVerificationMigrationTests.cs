using System.Diagnostics;
using Npgsql;
using Atlas.Server.Features.PageVerifications.Domain;
using Atlas.Server.Features.PageVerifications.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PageVerifications;

/// <summary>Migration 2026112400 over an installation's rows (plan §8): every space keeps verification off and no page gets a row, so
/// every existing page is „off“; and the perf guard of the space's readership (plan §5.2).</summary>
public sealed class PageVerificationMigrationTests
{
    private const long Version = 2026112400;
    private const string Writer = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a";

    [PostgreSqlFact]
    public async Task Existing_spaces_keep_verification_off_and_existing_pages_are_off()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_page_verification_upgrade_");
        await Atlas.Server.Tests.Events.OutboxTests.ApplyBeforeAsync(schema.ConnectionString, Version);
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES ('{Writer}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('00000000-0000-0000-0000-0000000000a1','{Space}','{Writer}',1);
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal(DBNull.Value, await ScalarAsync(schema.ConnectionString, $"""SELECT "VerificationIntervalMonths" FROM "Space" WHERE "Id"='{Space}'"""));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "PageVerification" """));
        // No row, no interval: off, as the reader computes it.
        Assert.Equal(PageVerificationStates.Off, PageVerificationStates.Of(PageVerificationStates.Interval(null, null, false), null, DateTime.UtcNow));
        // The checks hold: an interval outside the choices is refused.
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString, $"""UPDATE "Space" SET "VerificationIntervalMonths"=5 WHERE "Id"='{Space}'"""));
    }

    /// <summary>Plan §5.2: the readership of a space of 5 000 pages over 365 days without daily aggregates stays under 300 ms; over it, an
    /// aggregate table is due. The best of three runs, so a cold cache or a busy machine does not decide.</summary>
    [PostgreSqlFact]
    public async Task The_readership_of_a_space_of_5000_pages_over_a_year_stays_under_300_ms()
    {
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_readership_budget_");
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              SELECT md5('u'||i)::uuid,'reader'||i,'Reader '||i,i=0,true,'stamp' FROM generate_series(0,199) i;
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','Velký','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") SELECT md5('p'||i)::uuid,'{Space}',md5('u0')::uuid,1 FROM generate_series(1,5000) i;
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              SELECT md5('r'||i)::uuid,md5('p'||i)::uuid,1,'Stránka '||i,'{"{}"}','x',md5('u0')::uuid,now()-interval '400 days','web' FROM generate_series(1,5000) i;
            UPDATE "Page" p SET "PublishedRevisionId"=r."Id" FROM "PageRevision" r WHERE r."PageId"=p."Id";
            -- 200 000 views: 40 a page on average, the most read pages far more, spread over the year.
            INSERT INTO "PageView"("Id","PageId","UserId","ViewedAt")
              SELECT gen_random_uuid(),md5('p'||(1+floor(5000*power(random(),3)))::int)::uuid,md5('u'||floor(random()*200)::int)::uuid,
                now()-random()*interval '365 days' FROM generate_series(1,200000);
            ANALYZE;
            """);
        var admin = Guid.Parse(await ScalarAsync(schema.ConnectionString, "SELECT md5('u0')::uuid::text") as string ?? "");
        var best = TimeSpan.MaxValue;
        long pages = 0;
        for (var run = 0; run < 3; run++)
        {
            await using var connection = new NpgsqlConnection(schema.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(SpaceReadershipHandler.SpaceReadsSql, connection);
            command.Parameters.AddWithValue("actor", admin);
            command.Parameters.AddWithValue("space", Guid.Parse(Space));
            command.Parameters.AddWithValue("start", DateTime.UtcNow.AddDays(-365));
            command.Parameters.AddWithValue("end", DateTime.UtcNow.AddDays(1));
            var watch = Stopwatch.StartNew();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                pages = 0;
                while (await reader.ReadAsync())
                {
                    pages++;
                }
            }
            best = watch.Elapsed < best ? watch.Elapsed : best;
        }
        Assert.Equal(5000, pages);
        Assert.True(best < TimeSpan.FromMilliseconds(300), $"The space's readership took {best.TotalMilliseconds:F0} ms (budget 300 ms).");
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
