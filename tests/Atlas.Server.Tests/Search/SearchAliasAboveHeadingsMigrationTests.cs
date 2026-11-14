using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Search;

public sealed class SearchAliasAboveHeadingsMigrationTests
{
    private const string Owner = "00000000-0000-0000-0000-000000000001";
    private const string Space = "00000000-0000-0000-0000-000000000002";
    private const string Renamed = "00000000-0000-0000-0000-000000000003";
    private const string OldRevision = "00000000-0000-0000-0000-000000000004";
    private const string NewRevision = "00000000-0000-0000-0000-000000000005";
    private const string Headed = "00000000-0000-0000-0000-000000000006";
    private const string HeadedRevision = "00000000-0000-0000-0000-000000000007";
    private const string Search = "SELECT string_agg(hit.page_id::text,',' ORDER BY hit.position) FROM atlas_search_published('kbn','kbn%','kbn:*',false,true,'"
        + Owner + "','stamp',NULL,10,0) WITH ORDINALITY AS hit(page_id,space_id,space_name,title,snippet,changed_at,heading_title,heading_id,matched_alias,position)";

    [PostgreSqlFact]
    public async Task An_existing_page_found_by_its_earlier_title_moves_above_a_heading_match()
    {
        var schema = "atlas_migration_" + Guid.NewGuid().ToString("N");
        var connectionString = new NpgsqlConnectionStringBuilder(await Platform.TestDatabases.CreateEmptyAsync(schema)) { SearchPath = schema + ",public" }.ConnectionString;
        try
        {
            await ExecuteAsync(connectionString, "CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
            var assembly = typeof(AtlasDatabase).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                         .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                         .Where(item => item.Version < 2026102720).OrderBy(item => item.Version))
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource.Name)!);
                await ExecuteAsync(connectionString, await reader.ReadToEndAsync());
                await ExecuteAsync(connectionString, $"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
            }
            // 2.15 data: a page renamed from "Deployment in KBN" and a page with a "KBN" heading.
            await ExecuteAsync(connectionString, $"""
                INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','owner','Owner',true,true,'stamp');
                INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','Space','book-open','blue');
                INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Renamed}','{Space}','{Owner}',2),('{Headed}','{Space}','{Owner}',1);
                INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel")
                  VALUES('{OldRevision}','{Renamed}',1,'Deployment in KBN','{"{}"}','Servery.','{Owner}','2026-01-01T00:00:00Z','2026-01-02T00:00:00Z','web'),
                        ('{NewRevision}','{Renamed}',2,'Nemocnice Na Kopečku','{"{}"}','Servery.','{Owner}','2026-01-02T00:00:00Z',NULL,'web'),
                        ('{HeadedRevision}','{Headed}',1,'External access','{"{}"}',E'## KBN\n\nAdresy portálu.','{Owner}','2026-01-01T00:00:00Z',NULL,'web');
                INSERT INTO "PagePublication"("Id","PageId","RevisionId","PublisherId","ActiveFrom","ActiveTo","Channel")
                  VALUES(gen_random_uuid(),'{Renamed}','{OldRevision}','{Owner}','2026-01-01T00:00:00Z','2026-01-02T00:00:00Z','web'),
                        (gen_random_uuid(),'{Renamed}','{NewRevision}','{Owner}','2026-01-02T00:00:00Z',NULL,'web'),
                        (gen_random_uuid(),'{Headed}','{HeadedRevision}','{Owner}','2026-01-01T00:00:00Z',NULL,'web');
                UPDATE "Page" SET "PublishedRevisionId"='{NewRevision}' WHERE "Id"='{Renamed}';
                UPDATE "Page" SET "PublishedRevisionId"='{HeadedRevision}' WHERE "Id"='{Headed}';
                """);
            Assert.Equal($"{Headed},{Renamed}", await ScalarAsync(connectionString, Search));

            await AtlasMigrations.MigrateAsync(connectionString);

            // The search is C# query text since migration 2026110935.
            var moved = MovedSearch.Bind(Atlas.Server.Features.Search.SearchQueries.Published,
                [.. MovedSearch.Lexical("kbn", "kbn%", "kbn:*", false, true, Owner, "stamp"), ("take", "10"), ("skip", "0")]);
            Assert.Equal($"{Renamed},{Headed}", await ScalarAsync(connectionString,
                $"SELECT string_agg(hit.page_id::text,',' ORDER BY hit.position) FROM (SELECT moved.page_id,row_number() OVER () AS position FROM ({moved}) moved) hit"));
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
