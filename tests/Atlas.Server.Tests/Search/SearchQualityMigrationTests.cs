using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Search;

public sealed class SearchQualityMigrationTests
{
    private const string Owner = "00000000-0000-0000-0000-000000000001";
    private const string Space = "00000000-0000-0000-0000-000000000002";
    private const string ApiPage = "00000000-0000-0000-0000-000000000003";
    private const string ApiRevision = "00000000-0000-0000-0000-000000000004";
    private const string PlainPage = "00000000-0000-0000-0000-000000000005";
    private const string PlainRevision = "00000000-0000-0000-0000-000000000006";
    private const string EmptyJson = "{}";

    [PostgreSqlFact]
    public async Task Existing_published_openapi_pages_become_searchable_and_other_rows_stay()
    {
        var schema = "atlas_migration_" + Guid.NewGuid().ToString("N");
        var connectionString = new NpgsqlConnectionStringBuilder(await Platform.TestDatabases.CreateEmptyAsync(schema)) { SearchPath = schema + ",public" }.ConnectionString;
        try
        {
            await ExecuteAsync(connectionString, "CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
            var assembly = typeof(AtlasDatabase).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                         .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                         .Where(item => item.Version < 2026102010).OrderBy(item => item.Version))
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource.Name)!);
                await ExecuteAsync(connectionString, await reader.ReadToEndAsync());
                await ExecuteAsync(connectionString, $"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
            }
            // 2.14 data: a published page with an OpenAPI component (its Markdown holds only the summary) and a plain page.
            var content = OpenApiDocuments.Rich("openapi: 3.1.0\ninfo:\n  title: Kalendář\n  version: '10.3.0'\npaths:\n  /freebusy:\n    get:\n      summary: Obsazenost\n").Replace("'", "''");
            await ExecuteAsync(connectionString, $"""
                INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','owner','Owner',false,true,'stamp');
                INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','Space','book-open','blue');
                INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{ApiPage}','{Space}','{Owner}',1),('{PlainPage}','{Space}','{Owner}',1);
                INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
                  VALUES('{ApiRevision}','{ApiPage}',1,'API','{content}','Popis API\n\nKalendář · v10.3.0 · 1 operací · 0 schémat','{Owner}','2026-01-01T00:00:00Z','web'),
                        ('{PlainRevision}','{PlainPage}',1,'Plain','{EmptyJson}','plain text','{Owner}','2026-01-01T00:00:00Z','web');
                UPDATE "Page" SET "PublishedRevisionId"='{ApiRevision}' WHERE "Id"='{ApiPage}';
                UPDATE "Page" SET "PublishedRevisionId"='{PlainRevision}' WHERE "Id"='{PlainPage}';
                """);
            Assert.Equal(false, await ScalarAsync(connectionString, "SELECT \"Vector\" @@ to_tsquery('simple','freebusy') FROM \"SearchDocument\" WHERE \"PageId\"='" + ApiPage + "'"));
            var plainBefore = await ScalarAsync(connectionString, "SELECT \"Body\" || '|' || \"Vector\"::text FROM \"SearchDocument\" WHERE \"PageId\"='" + PlainPage + "'");

            await AtlasMigrations.MigrateAsync(connectionString);

            Assert.Equal(true, await ScalarAsync(connectionString, "SELECT \"Vector\" @@ to_tsquery('simple','freebusy & obsazenost') FROM \"SearchDocument\" WHERE \"PageId\"='" + ApiPage + "'"));
            Assert.Equal(true, await ScalarAsync(connectionString, "SELECT \"Body\" LIKE 'Popis API%summary: Obsazenost%' FROM \"SearchDocument\" WHERE \"PageId\"='" + ApiPage + "'"));
            Assert.Equal(plainBefore, await ScalarAsync(connectionString, "SELECT \"Body\" || '|' || \"Vector\"::text FROM \"SearchDocument\" WHERE \"PageId\"='" + PlainPage + "'"));
            // New publications are written by the code since migration 2026110935 (SearchDocuments, here its rebuild).
            await ExecuteAsync(connectionString, $"""
                UPDATE "PageRevision" SET "ActiveTo"='2026-02-01T00:00:00Z' WHERE "Id"='{PlainRevision}';
                INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
                  VALUES('00000000-0000-0000-0000-000000000007','{PlainPage}',2,'Plain','{content}','plain text','{Owner}','2026-02-01T00:00:00Z','web');
                UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-000000000007',"Version"=2 WHERE "Id"='{PlainPage}';
                {Atlas.Server.Features.Search.SearchDocuments.RebuildAllSql};
                """);
            Assert.Equal(true, await ScalarAsync(connectionString, "SELECT \"Vector\" @@ to_tsquery('simple','freebusy') FROM \"SearchDocument\" WHERE \"PageId\"='" + PlainPage + "'"));
        }
        finally
        {
            await Platform.TestDatabases.DropAsync(connectionString);
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
