using Npgsql;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>Migration 2026110670 over the live states an installation already has: a stored snapshot and an empty state both stay, the
/// unread Markdown and plain-text projections go, and the state, its hash and its editor JSON are still all set or all empty.</summary>
public sealed class CollaborationProjectionColumnsMigrationTests
{
    [PostgreSqlFact]
    public async Task Existing_states_stay_without_the_unread_projections()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_collab_projection_columns_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110670);
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
            INSERT INTO "PageCollaboration"("PageId","Generation","BaseRevisionId","Sequence","SnapshotSequence","State","StateSha256","ProjectionJson",
                "ProjectionMarkdown","ProjectionText") VALUES
              ('00000000-0000-0000-0000-0000000000a1','00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000b1',2,2,
                decode('0102','hex'),repeat('a',64),'{"type":"doc"}','Snapshot',''),
              ('00000000-0000-0000-0000-0000000000a2','00000000-0000-0000-0000-0000000000c2','00000000-0000-0000-0000-0000000000b2',0,0,NULL,NULL,NULL,NULL,NULL);
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("a1:2:{\"type\":\"doc\"}|a2:0:-", await ScalarAsync(schema.ConnectionString, """
            SELECT string_agg(right("PageId"::text, 2) || ':' || "SnapshotSequence" || ':' || coalesce("ProjectionJson", '-'), '|' ORDER BY "PageId")
            FROM "PageCollaboration"
            """));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """
            SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'PageCollaboration'
              AND column_name IN ('ProjectionMarkdown', 'ProjectionText')
            """));
        // The rule of the dropped CHECK holds for what stays: a state without its editor JSON is refused.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString,
            """UPDATE "PageCollaboration" SET "ProjectionJson" = NULL WHERE "PageId" = '00000000-0000-0000-0000-0000000000a1'"""));
        Assert.Equal("23514", refused.SqlState);
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
