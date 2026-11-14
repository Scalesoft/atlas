using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Pages;

public sealed class ContentChannelMigrationTests
{
    private const string Owner = "00000000-0000-0000-0000-000000000001";
    private const string Page = "00000000-0000-0000-0000-000000000003";
    private const string Revision = "00000000-0000-0000-0000-000000000004";
    private const string Token = "00000000-0000-0000-0000-000000000005";
    private const string EmptyJson = "{}";

    [PostgreSqlFact]
    public async Task Existing_rows_are_legacy_and_new_inserts_need_a_channel()
    {
        var schema = "atlas_migration_" + Guid.NewGuid().ToString("N");
        var connectionString = new NpgsqlConnectionStringBuilder(await Platform.TestDatabases.CreateEmptyAsync(schema)) { SearchPath = schema + ",public" }.ConnectionString;
        try
        {
            await ExecuteAsync(connectionString, "CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
            var assembly = typeof(AtlasDatabase).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                         .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                         .Where(item => item.Version < 2026100610).OrderBy(item => item.Version))
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource.Name)!);
                await ExecuteAsync(connectionString, await reader.ReadToEndAsync());
                await ExecuteAsync(connectionString, $"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
            }
            // 1.5 data: a published revision, an opening and a personal API token that changed nothing yet.
            await ExecuteAsync(connectionString, $"""
                INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','owner','Owner',false,true,'stamp');
                INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-000000000002','Space','book-open','blue');
                INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Page}','00000000-0000-0000-0000-000000000002','{Owner}',1);
                INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom")
                  VALUES('{Revision}','{Page}',1,'Legacy','{EmptyJson}','legacy','{Owner}','2026-01-01T00:00:00Z');
                INSERT INTO "PagePublication"("Id","PageId","RevisionId","PublisherId","ActiveFrom") VALUES(gen_random_uuid(),'{Page}','{Revision}','{Owner}','2026-01-01T00:00:00Z');
                UPDATE "Page" SET "PublishedRevisionId"='{Revision}' WHERE "Id"='{Page}';
                INSERT INTO "PageAccessEvent"("PageId","RevisionId","ActorId","Action") VALUES('{Page}','{Revision}','{Owner}','page.opened');
                INSERT INTO "PersonalApiToken"("Id","UserId","Name","TokenHash","Prefix","SecurityStamp","ActiveFrom","ExpiresAt")
                  VALUES('{Token}','{Owner}','Script',repeat('A',64),'atl_test','stamp',now(),now() + interval '1 day');
                """);
            var fileNode = await ScalarAsync(connectionString, "SELECT pg_relation_filenode('\"PageRevision\"')");

            await AtlasMigrations.MigrateAsync(connectionString);

            // No rewrite: the same relation file, the old rows answer 'legacy' from the catalog, and no column keeps a default.
            Assert.Equal(fileNode, await ScalarAsync(connectionString, "SELECT pg_relation_filenode('\"PageRevision\"')"));
            Assert.Equal("legacy:-|legacy:-|-:-", await ScalarAsync(connectionString, """
                SELECT (SELECT "Channel" || ':' || coalesce("ApiTokenId"::text,'-') FROM "PageRevision") || '|' ||
                       (SELECT "Channel" || ':' || coalesce("ApiTokenId"::text,'-') FROM "PagePublication") || '|' ||
                       (SELECT coalesce("Channel",'-') || ':' || coalesce("ApiTokenId"::text,'-') FROM "PageAccessEvent")
                """));
            Assert.Equal(0L, await ScalarAsync(connectionString, """
                SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema()
                  AND table_name IN ('PageRevision','PagePublication','PageAccessEvent') AND column_name = 'Channel' AND column_default IS NOT NULL
                """));

            // Every insert names its channel, and only api and mcp carry a token.
            await RejectedAsync(connectionString, PostgresErrorCodes.NotNullViolation, Insert(null, null));
            await RejectedAsync(connectionString, PostgresErrorCodes.CheckViolation, Insert("'legacy-x'", null));
            await RejectedAsync(connectionString, PostgresErrorCodes.CheckViolation, Insert("'api'", null));
            await RejectedAsync(connectionString, PostgresErrorCodes.CheckViolation, Insert("'web'", $"'{Token}'"));
            await RejectedAsync(connectionString, PostgresErrorCodes.CheckViolation,
                $"INSERT INTO \"PageAccessEvent\"(\"PageId\",\"RevisionId\",\"ActorId\",\"Action\",\"Channel\") VALUES('{Page}','{Revision}','{Owner}','page.opened','mcp')");
            await ExecuteAsync(connectionString, Insert("'mcp'", $"'{Token}'"));
            await ExecuteAsync(connectionString,
                $"INSERT INTO \"PageAccessEvent\"(\"PageId\",\"RevisionId\",\"ActorId\",\"Action\",\"Channel\",\"ApiTokenId\") VALUES('{Page}','{Revision}','{Owner}','page.opened','mcp','{Token}')");
        }
        finally
        {
            await Platform.TestDatabases.DropAsync(connectionString);
        }
    }

    /// <summary>A closed revision before the legacy one: valid apart from what the channel columns say.</summary>
    private static string Insert(string? channel, string? token)
    {
        var columns = channel is null ? "" : ",\"Channel\",\"ApiTokenId\"";
        var values = channel is null ? "" : $",{channel},{token ?? "NULL"}";
        return "INSERT INTO \"PageRevision\"(\"Id\",\"PageId\",\"Version\",\"Title\",\"ContentJson\",\"Markdown\",\"AuthorId\",\"ActiveFrom\",\"ActiveTo\"" + columns + ") "
            + $"VALUES(gen_random_uuid(),'{Page}',2,'New','{EmptyJson}','new','{Owner}','2025-01-01T00:00:00Z','2025-06-01T00:00:00Z'{values})";
    }
    private static async Task RejectedAsync(string connectionString, string sqlState, string sql)
        => Assert.Equal(sqlState, (await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, sql))).SqlState);

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static Task ExecuteAsync(string connectionString, string sql) => ScalarAsync(connectionString, sql);
}
