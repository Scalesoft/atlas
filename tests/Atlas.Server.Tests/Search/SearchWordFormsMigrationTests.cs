using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Search;

public sealed class SearchWordFormsMigrationTests
{
    private const string Owner = "00000000-0000-0000-0000-000000000001";
    private const string Space = "00000000-0000-0000-0000-000000000002";
    private const string Published = "00000000-0000-0000-0000-000000000003";
    private const string PublishedRevision = "00000000-0000-0000-0000-000000000004";
    private const string Draft = "00000000-0000-0000-0000-000000000005";
    private const string DraftRevision = "00000000-0000-0000-0000-000000000006";

    [PostgreSqlFact]
    public async Task Existing_published_and_working_rows_get_word_forms_and_identifier_parts()
    {
        var schema = "atlas_migration_" + Guid.NewGuid().ToString("N");
        var connectionString = new NpgsqlConnectionStringBuilder(await Platform.TestDatabases.CreateEmptyAsync(schema)) { SearchPath = schema + ",public" }.ConnectionString;
        try
        {
            await ExecuteAsync(connectionString, "CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
            var assembly = typeof(AtlasDatabase).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                         .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                         .Where(item => item.Version < 2026102030).OrderBy(item => item.Version))
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource.Name)!);
                await ExecuteAsync(connectionString, await reader.ReadToEndAsync());
                await ExecuteAsync(connectionString, $"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
            }
            // 2.15 data: a published page and a never published draft (its working projection).
            await ExecuteAsync(connectionString, $"""
                INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','owner','Owner',false,true,'stamp');
                INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','Space','book-open','blue');
                INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Published}','{Space}','{Owner}',1),('{Draft}','{Space}','{Owner}',1);
                INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
                  VALUES('{PublishedRevision}','{Published}',1,'Smlouvy','{"{}"}',E'## Tiskárny\n\nMetoda getFreeBusy vrací obsazenost.','{Owner}','2026-01-01T00:00:00Z','web'),
                        ('{DraftRevision}','{Draft}',1,'Koncept','{"{}"}','Volání export_page_pdf dostane tiskárnu.','{Owner}','2026-01-01T00:00:00Z','web');
                UPDATE "Page" SET "PublishedRevisionId"='{PublishedRevision}' WHERE "Id"='{Published}';
                """);
            const string published = "SELECT \"Vector\" @@ to_tsquery('simple','smlouv & freebusy & free & busy') AND \"HeadingVector\" @@ to_tsquery('simple','tiskarn') FROM \"SearchDocument\" WHERE \"PageId\"='" + Published + "'";
            const string working = "SELECT \"Vector\" @@ to_tsquery('simple','tiskarn & exportpagepdf & pagepdf') FROM \"WorkingSearchDocument\" WHERE \"PageId\"='" + Draft + "'";
            Assert.Equal(false, await ScalarAsync(connectionString, published));
            Assert.Equal(false, await ScalarAsync(connectionString, working));

            await AtlasMigrations.MigrateAsync(connectionString);

            Assert.Equal(true, await ScalarAsync(connectionString, published));
            Assert.Equal(true, await ScalarAsync(connectionString, working));
            // The exact words stay, ahead of their variants.
            Assert.Equal(true, await ScalarAsync(connectionString, "SELECT \"Vector\" @@ to_tsquery('simple','smlouvy & getfreebusy & metoda <-> getfreebusy') FROM \"SearchDocument\" WHERE \"PageId\"='" + Published + "'"));
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
