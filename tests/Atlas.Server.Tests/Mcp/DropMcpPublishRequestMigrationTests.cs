using Atlas.Server.Features.PersonalApi.Domain;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Mcp;

/// <summary>Migration 2026110685 over existing rows: the web confirmations of MCP publications go, the page and the token stay.</summary>
public sealed class DropMcpPublishRequestMigrationTests
{
    [PostgreSqlFact]
    public async Task Existing_publish_requests_are_dropped_and_their_page_and_token_stay()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_drop_mcp_publish_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110685);
        const string User = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a",
            Page = "00000000-0000-0000-0000-0000000000f1", Token = "00000000-0000-0000-0000-0000000000c1";
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{User}','owner','Owner',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Page}','{Space}','{User}',1);
            INSERT INTO "PersonalApiToken"("Id","UserId","Name","TokenHash","Prefix","SecurityStamp","ActiveFrom","ActiveTo","ExpiresAt")
              VALUES('{Token}','{User}','Agent','{ApiSecret.Hash(ApiSecret.Create())}','sat_00000000','stamp',now(),NULL,now()+interval '30 days');
            INSERT INTO "McpPublishRequest"("Id","TokenId","UserId","PageId","Version","CreatedAt","ExpiresAt")
              VALUES(gen_random_uuid(),'{Token}','{User}','{Page}',1,now(),now()+interval '10 minutes');
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("0|1|1", await OutboxTests.ScalarAsync(schema.ConnectionString, """
            SELECT (SELECT count(*) FROM pg_tables WHERE tablename='McpPublishRequest') || '|' ||
                   (SELECT count(*) FROM "Page") || '|' || (SELECT count(*) FROM "PersonalApiToken")
            """));
        Assert.Equal(0L, await OutboxTests.ScalarAsync(schema.ConnectionString,
            "SELECT count(*) FROM pg_proc WHERE proname='atlas_mcp_publish_request_guard'"));
    }
}
