using Npgsql;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>Migration 2026110571 (3.6-H) over the live updates an installation already has: they stay, as browsers' (web: no channel,
/// token or conversation); a token goes only with api or mcp and a conversation only with assistant.</summary>
public sealed class CollaborationUpdateOriginMigrationTests
{
    [PostgreSqlFact]
    public async Task Existing_updates_stay_web_and_an_origin_must_agree_with_its_channel()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_collab_update_origin_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110571);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','admin','Admin',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','Space','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version","SortOrder") VALUES
              ('00000000-0000-0000-0000-0000000000a1','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1,1024);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-0000000000a1',1,'A','{"type":"doc","content":[]}','','00000000-0000-0000-0000-000000000001',now(),'web');
            INSERT INTO "PageCollaboration"("PageId","Generation","BaseRevisionId","Sequence","SnapshotSequence") VALUES
              ('00000000-0000-0000-0000-0000000000a1','00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000b1',2,0);
            INSERT INTO "CollaborationUpdate"("PageId","Generation","Sequence","ClientUpdateId","UserId","ReceivedAt","Payload")
            SELECT '00000000-0000-0000-0000-0000000000a1','00000000-0000-0000-0000-0000000000c1',n,gen_random_uuid(),
              '00000000-0000-0000-0000-000000000001',now(),decode('01','hex')
            FROM generate_series(1,2) n;
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("1:::|2:::", await ScalarAsync(schema.ConnectionString, """
            SELECT string_agg("Sequence" || ':' || coalesce("Channel",'') || ':' || coalesce("ApiTokenId"::text,'') || ':' || coalesce("AiConversationId"::text,''),
              '|' ORDER BY "Sequence") FROM "CollaborationUpdate"
            """));
        await ExecuteAsync(schema.ConnectionString, """
            UPDATE "CollaborationUpdate" SET "Channel"='mcp', "ApiTokenId"=gen_random_uuid() WHERE "Sequence"=1;
            UPDATE "CollaborationUpdate" SET "Channel"='assistant', "AiConversationId"=gen_random_uuid() WHERE "Sequence"=2;
            """);
        foreach (var refused in new[]
                 {
                     "\"Channel\"='web', \"ApiTokenId\"=gen_random_uuid(), \"AiConversationId\"=NULL",
                     "\"Channel\"='mcp', \"ApiTokenId\"=NULL, \"AiConversationId\"=gen_random_uuid()",
                     "\"Channel\"='ai', \"ApiTokenId\"=NULL, \"AiConversationId\"=NULL",
                 })
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString,
                $"""UPDATE "CollaborationUpdate" SET {refused} WHERE "Sequence"=1"""));
            Assert.Equal("23514", failure.SqlState);
        }
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
