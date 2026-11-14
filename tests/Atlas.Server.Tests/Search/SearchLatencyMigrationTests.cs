using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Search;

/// <summary>Migrations 2026102873 (UX SE3-1), 2026102890 (VYK1-2, with the trigram index of the body head), 2026102895 (VYK2-1,
/// the body test in its own CTE) and 2026110686 (the snippet's window of the body) replace only search functions and indexes: rows
/// indexed before them are found by them as before.</summary>
public sealed class SearchLatencyMigrationTests
{
    private const string Owner = "00000000-0000-0000-0000-000000000001";
    private const string Space = "00000000-0000-0000-0000-000000000002";
    private const string Published = "00000000-0000-0000-0000-000000000003";
    private const string PublishedRevision = "00000000-0000-0000-0000-000000000004";
    private const string Draft = "00000000-0000-0000-0000-000000000005";
    private const string DraftRevision = "00000000-0000-0000-0000-000000000006";
    private const string Long = "00000000-0000-0000-0000-000000000007";
    private const string LongRevision = "00000000-0000-0000-0000-000000000008";

    [PostgreSqlTheory]
    [InlineData(2026102873L)]
    [InlineData(2026102890L)]
    [InlineData(2026102895L)]
    [InlineData(2026110686L)]
    public async Task Pages_indexed_before_the_migration_are_found_by_its_functions(long migration)
    {
        var schema = "atlas_migration_" + Guid.NewGuid().ToString("N");
        var connectionString = new NpgsqlConnectionStringBuilder(await Platform.TestDatabases.CreateEmptyAsync(schema)) { SearchPath = schema + ",public" }.ConnectionString;
        try
        {
            await ExecuteAsync(connectionString, "CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
            var assembly = typeof(AtlasDatabase).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                         .Select(name => (Name: name, Version: long.Parse(name.Split('.')[^2].Split('_')[0])))
                         .Where(item => item.Version < migration).OrderBy(item => item.Version))
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource.Name)!);
                await ExecuteAsync(connectionString, await reader.ReadToEndAsync());
                await ExecuteAsync(connectionString, $"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
            }
            await ExecuteAsync(connectionString, $"""
                INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','owner','Owner',true,true,'stamp');
                INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','Space','book-open','blue');
                INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Published}','{Space}','{Owner}',1),('{Draft}','{Space}','{Owner}',1);
                INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
                  VALUES('{PublishedRevision}','{Published}',1,'Kalkulačky','{"{}"}',E'## Úvod\n\nStará kalkulakca v kanceláři.','{Owner}','2026-01-01T00:00:00Z','web'),
                        ('{DraftRevision}','{Draft}',1,'Koncept','{"{}"}','Rozepsaná kalkulakca.','{Owner}','2026-01-01T00:00:00Z','web');
                INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Long}','{Space}','{Owner}',1);
                INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
                  VALUES('{LongRevision}','{Long}',1,'Dlouhá','{"{}"}',repeat('slovo ',6000) || 'zahrada na konci','{Owner}','2026-01-01T00:00:00Z','web');
                UPDATE "Page" SET "PublishedRevisionId"='{PublishedRevision}' WHERE "Id"='{Published}';
                UPDATE "Page" SET "PublishedRevisionId"='{LongRevision}' WHERE "Id"='{Long}';
                """);

            await AtlasMigrations.MigrateAsync(connectionString);

            // The search is C# query text since migration 2026110935, called here with the functions' arguments.
            var args = MovedSearch.Lexical("kalkulacka", "kalkulacka%", "(kalkulacka:* | kalkulack)", false, true, Owner, "stamp");
            string Ids(string text) => $"SELECT string_agg(moved.page_id::text, ',') FROM ({MovedSearch.Bind(text, [.. args, .. MovedSearch.Window(21, 0)])}) moved";
            // A strict title hit, then the draft found only by its misspelt body (the fuzzy test runs: one strict hit < 21).
            Assert.Equal(Published, (await SearchAsync(connectionString, Ids(Atlas.Server.Features.Search.SearchQueries.Published)))?.ToString());
            Assert.Equal(Draft, (await SearchAsync(connectionString, Ids(Atlas.Server.Features.Search.SearchQueries.Drafts())))?.ToString());
            Assert.Equal($"{Published},{Draft}", (await SearchAsync(connectionString, Ids(Atlas.Server.Features.Search.SearchQueries.All)))?.ToString());
            // A match 36 000 characters into the body is the snippet's, not the head of the page.
            var snippet = (await SearchAsync(connectionString, $"SELECT moved.snippet FROM ({MovedSearch.Bind(Atlas.Server.Features.Search.SearchQueries.Published,
                [.. MovedSearch.Lexical("zahrada", "zahrada%", "zahrada:*", false, false, Owner, "stamp"), ("take", "21"), ("skip", "0")])}) moved"))?.ToString();
            Assert.Contains("zahrada", snippet);
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

    /// <summary>A statement under the search transaction's settings (SearchSql.PrepareAsync).</summary>
    private static async Task<object?> SearchAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var settings = new NpgsqlCommand("SELECT set_config('pg_trgm.word_similarity_threshold','0.45',false), set_config('plan_cache_mode','force_custom_plan',false)", connection))
        {
            await settings.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static Task ExecuteAsync(string connectionString, string sql) => ScalarAsync(connectionString, sql);
}
