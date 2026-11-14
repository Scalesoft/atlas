using System.Globalization;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Atlas.Server.Tests.Search;

/// <summary>
/// Migration 2026102810 (review P2-9): NHibernate writes every column of a page it saves, so a rename, a move within the space or an
/// access change re-projected the published search document (160–195 ms for a 500 KB page). The document is now written only when
/// the publication or the space changes, or when a restore rebuilds the projections; existing documents stay as they are.
/// </summary>
public sealed class SearchDocumentTriggerMigrationTests(ITestOutputHelper output)
{
    private const string Owner = "00000000-0000-0000-0000-000000000001";
    private const string Space = "00000000-0000-0000-0000-000000000002";
    private const string Page = "00000000-0000-0000-0000-000000000003";
    private const string First = "00000000-0000-0000-0000-000000000004";
    private const string Second = "00000000-0000-0000-0000-000000000005";
    private const string Document = "SELECT xmin::text || ':' || \"RevisionId\" FROM \"SearchDocument\" WHERE \"PageId\"='" + Page + "'";
    private const string Touch = "UPDATE \"Page\" SET \"PublishedRevisionId\"=\"PublishedRevisionId\",\"SpaceId\"=\"SpaceId\",\"Version\"=\"Version\" WHERE \"Id\"='" + Page + "'";

    [PostgreSqlFact]
    public async Task A_page_update_that_keeps_the_publication_leaves_the_search_document_and_a_rebuild_still_writes_it()
    {
        var schema = "atlas_migration_" + Guid.NewGuid().ToString("N");
        var connectionString = new NpgsqlConnectionStringBuilder(await Platform.TestDatabases.CreateEmptyAsync(schema)) { SearchPath = schema + ",public" }.ConnectionString;
        try
        {
            await ExecuteAsync(connectionString, "CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
            var assembly = typeof(AtlasDatabase).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                         .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0], CultureInfo.InvariantCulture)))
                         .Where(item => item.Version < 2026102810).OrderBy(item => item.Version))
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource.Name)!);
                await ExecuteAsync(connectionString, await reader.ReadToEndAsync());
                await ExecuteAsync(connectionString, $"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
            }
            // A published page of 500 KB with a second revision to publish later.
            var body = string.Concat(Enumerable.Range(0, 6_250).Select(i => $"Odstavec {i:D5} prostého textu stránky, jen poznámky.\n\n"));
            await ExecuteAsync(connectionString, $"""
                INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','owner','Owner',false,true,'stamp');
                INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','Space','book-open','blue');
                INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Page}','{Space}','{Owner}',2);
                INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel")
                  VALUES('{First}','{Page}',1,'Velká','{"{}"}','{body}','{Owner}','2026-01-01T00:00:00Z','2026-01-02T00:00:00Z','web'),
                        ('{Second}','{Page}',2,'Velká 2','{"{}"}','Nová verze.','{Owner}','2026-01-02T00:00:00Z',NULL,'web');
                UPDATE "Page" SET "PublishedRevisionId"='{First}' WHERE "Id"='{Page}';
                """);
            var before = (string)(await ScalarAsync(connectionString, Document))!;
            output.WriteLine("before:\n" + await ScalarAsync(connectionString, "EXPLAIN (ANALYZE, FORMAT TEXT) " + Touch, all: true));
            Assert.NotEqual(before, await ScalarAsync(connectionString, Document));

            await AtlasMigrations.MigrateAsync(connectionString);

            var migrated = (string)(await ScalarAsync(connectionString, Document))!;
            Assert.EndsWith(First, migrated);
            var plan = await ScalarAsync(connectionString, "EXPLAIN (ANALYZE, FORMAT TEXT) " + Touch, all: true);
            output.WriteLine("after:\n" + plan);
            Assert.Equal(migrated, await ScalarAsync(connectionString, Document));
            Assert.DoesNotContain("TR_Page_SearchDocument", (string)plan!);
            // The restore's rebuild (InstallationModules, pages) asks for every document again.
            await ExecuteAsync(connectionString, "BEGIN; DELETE FROM \"PageContribution\"; " + Atlas.Server.Features.Backups.Installation.InstallationModules.PagesRebuild + "; COMMIT;");
            Assert.NotEqual(migrated, await ScalarAsync(connectionString, Document));
            // Since migration 2026110935 the code that publishes writes the document (SearchDocuments, as its rebuild here).
            await ExecuteAsync(connectionString, $"UPDATE \"Page\" SET \"PublishedRevisionId\"='{Second}' WHERE \"Id\"='{Page}'; "
                + Atlas.Server.Features.Search.SearchDocuments.RebuildAllSql);
            Assert.EndsWith(Second, (string)(await ScalarAsync(connectionString, Document))!);
        }
        finally
        {
            await Platform.TestDatabases.DropAsync(connectionString);
        }
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql, bool all = false)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        if (!all)
        {
            return await command.ExecuteScalarAsync();
        }
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }
        return string.Join("\n", lines);
    }

    private static Task ExecuteAsync(string connectionString, string sql) => ScalarAsync(connectionString, sql);
}
