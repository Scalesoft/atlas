using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Features.Comments.Persistence;
using Atlas.Server.Features.Search;
using Atlas.Server.Platform.Jobs;
using Xunit;

namespace Atlas.Server.Tests.Search;

/// <summary>
/// Migration 2026110935 (Atlas 3.6, no logic in the database): the search triggers and functions go, the code writes the documents
/// (SearchDocuments, BoardSearch, CommentWrites) and the existing rows stay. Over rows the triggers and functions wrote before it, the
/// C# rebuild (search.documents-rebuild, by hand) writes exactly the same documents (every column, vectors and headings included), and
/// a second rebuild changes nothing.
/// </summary>
public sealed class SearchWithoutDatabaseLogicMigrationTests
{
    private const string Owner = "00000000-0000-0000-0000-000000000001";
    private const string Space = "00000000-0000-0000-0000-000000000002";
    private const string Other = "00000000-0000-0000-0000-000000000003";
    /// <summary>The seed runs on the schema before the first 3.6 migration that moves database logic into code (the projector's,
    /// 2026110870): a 3.5 wiki, whose triggers still write every derived row (the page envelopes a comment refers to among them).</summary>
    private const long SeedBefore = 2026110870;

    /// <summary>Every derived column of the four document tables, by row.</summary>
    private const string Documents = """
        SELECT string_agg(x.row, E'\n' ORDER BY x.row) FROM (
          SELECT 'S ' || row_to_json(d)::text AS row FROM "SearchDocument" d
          UNION ALL SELECT 'W ' || row_to_json(w)::text FROM "WorkingSearchDocument" w
          UNION ALL SELECT 'B ' || row_to_json(b)::text FROM "BoardSearchDocument" b
          UNION ALL SELECT 'C ' || row_to_json(c)::text FROM "CommentSearchDocument" c) x
        """;

    [PostgreSqlFact]
    public async Task The_code_rebuilds_the_documents_the_triggers_wrote_exactly_and_a_second_run_changes_nothing()
    {
        var schema = "atlas_migration_" + Guid.NewGuid().ToString("N");
        var connectionString = new NpgsqlConnectionStringBuilder(await Platform.TestDatabases.CreateEmptyAsync(schema)) { SearchPath = schema + ",public" }.ConnectionString;
        try
        {
            await ExecuteAsync(connectionString, "CREATE TABLE \"SchemaVersion\" (\"Version\" bigint PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
            var assembly = typeof(AtlasDatabase).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                         .Select(name => (Name: name, Version: long.Parse(name.Split('.')[^2].Split('_')[0], CultureInfo.InvariantCulture)))
                         .Where(item => item.Version < SeedBefore).OrderBy(item => item.Version))
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource.Name)!);
                await ExecuteAsync(connectionString, await reader.ReadToEndAsync());
                await ExecuteAsync(connectionString, $"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
            }
            await SeedAsync(connectionString);
            var before = (string)(await ScalarAsync(connectionString, Documents))!;
            Assert.Equal(5, before.Split('\n').Count(row => row.StartsWith("S ", StringComparison.Ordinal)));
            // The rows hold what the rules make: anchors, an earlier title and a published import's path as aliases, component text.
            Assert.All(new[] { "\"nastaveni\"", "\"odkaz\"", "Starý Tiskárna v kanceláři", "navody/tiskarny/importovana-stranka", "Obsazenost" },
                text => Assert.Contains(text, before));
            Assert.DoesNotContain("zz/nepublikovana-cesta", before);

            await AtlasMigrations.MigrateAsync(connectionString);

            Assert.Equal(0L, await ScalarAsync(connectionString, """
                SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
                WHERE c.relnamespace = current_schema()::regnamespace AND NOT t.tgisinternal
                  AND (t.tgname LIKE '%Search%' OR t.tgname LIKE '%Aliases%')
                """));
            Assert.Equal(0L, await ScalarAsync(connectionString, """
                SELECT count(*) FROM pg_proc WHERE pronamespace = current_schema()::regnamespace
                  AND proname ~ '^atlas_(search|similar|link_candidates|rank_by_context|placement_parent|template_suggestion|answer_|ai_dependency|normalize_search|strip_secret|comment_search|refresh_(board|comment|published|working|search|publication|import)|project_working|update_search)'
                """));
            // The rows stay as they are (the migration queues no rebuild: the code computes the same rows, as below).
            Assert.Equal(before, await ScalarAsync(connectionString, Documents));

            await RebuildAsync(connectionString);
            var rebuilt = (string)(await ScalarAsync(connectionString, Documents))!;
            Assert.Equal(before.Split('\n'), rebuilt.Split('\n'));
            await RebuildAsync(connectionString);
            Assert.Equal(rebuilt, await ScalarAsync(connectionString, Documents));
        }
        finally
        {
            await Platform.TestDatabases.DropAsync(connectionString);
        }
    }

    /// <summary>The job over a live wiki: a damaged document comes back as it was, in batches, and the job can run again.</summary>
    [PostgreSqlFact]
    public async Task The_rebuild_job_restores_every_page_s_documents()
    {
        await using var f = await SearchFixture.CreateAsync(isolated: true); // the rebuild job rewrites the search documents of every page of the installation and deadlocks with tests writing pages beside it
        for (var i = 0; i < 60; i++)
        {
            var page = await f.Create($"Stránka {i} o tiskárnách");
            if (i % 3 == 0)
            {
                await f.Pages.PublishAsync(page.Id, new(page.Version), f.Writer, default);
            }
        }
        // The job rebuilds the whole wiki (harmless to the tests beside it); the damage and the comparison stay on this test's own pages.
        var ownIds = $"SELECT \"Id\" FROM \"Page\" WHERE \"SpaceId\" = '{f.SpaceId}'";
        var ownDocuments = $"""
            SELECT string_agg(x.row, E'\n' ORDER BY x.row) FROM (
              SELECT 'S ' || row_to_json(d)::text AS row FROM "SearchDocument" d WHERE d."PageId" IN ({ownIds})
              UNION ALL SELECT 'W ' || row_to_json(w)::text FROM "WorkingSearchDocument" w WHERE w."PageId" IN ({ownIds})) x
            """;
        var expected = await ScalarAsync(f.ConnectionString, ownDocuments);
        await ExecuteAsync(f.ConnectionString, $"""
            DELETE FROM "WorkingSearchDocument" WHERE "PageId" IN (SELECT "PageId" FROM "WorkingSearchDocument" WHERE "PageId" IN ({ownIds}) ORDER BY "PageId" LIMIT 30);
            UPDATE "SearchDocument" SET "Vector" = '', "Aliases" = '["x"]' WHERE "PageId" IN ({ownIds});
            """);
        Assert.NotEqual(expected, await ScalarAsync(f.ConnectionString, ownDocuments));

        var runner = f.Host.Services.GetRequiredService<IJobRunner>();
        await runner.RunToEndAsync(SearchDocumentsRebuildJob.JobId, "Test");
        Assert.Equal(expected, await ScalarAsync(f.ConnectionString, ownDocuments));
        await runner.RunToEndAsync(SearchDocumentsRebuildJob.JobId, "Test again");
        Assert.Equal(expected, await ScalarAsync(f.ConnectionString, ownDocuments));
    }

    /// <summary>What search.documents-rebuild and the boards' and comments' restore rebuilds write.</summary>
    private static Task RebuildAsync(string connectionString) => ExecuteAsync(connectionString, SearchDocuments.RebuildAllSql + ";\n"
        + BoardSearch.RefreshSql("IN (SELECT \"Id\" FROM \"Board\")") + ";\n" + CommentWrites.SearchRefreshSql("IN (SELECT \"Id\" FROM \"Comment\")"));

    /// <summary>
    /// Pages that exercise every rule of the documents: headings in and out of code fences, duplicate titles with their anchors, links
    /// and emphasis in headings, CRLF, password references, an OpenAPI component, technical words and identifiers, accents, a page
    /// published under three titles, the source path of a published and of an unpublished import, a draft beside a publication, a
    /// page only drafted, a board and a comment. Written while the triggers ran, as on a wiki before 3.6.
    /// </summary>
    private static async Task SeedAsync(string connectionString)
    {
        static string Rich(params object[] nodes) => JsonSerializer.Serialize(new { format = "scalesoft-rich", version = 1, document = new { type = "doc", content = nodes } });
        static object Heading(string text, string? id) => new { type = "heading", attrs = new { level = 2, headingId = id }, content = new[] { new { type = "text", text } } };
        static object Paragraph(string text) => new { type = "paragraph", content = new[] { new { type = "text", text } } };
        var openApi = new { type = "openapi", attrs = new { data = new { source = "openapi: 3.0.0\ninfo:\n  title: Kalendář\npaths:\n  /getFreeBusy:\n    get:\n      summary: Obsazenost místností" } } };
        var nested = new { type = "details", content = new object[] { Heading("Skrytý nadpis", "skryty"), Paragraph("x") } };
        var pages = new (string Id, string Title, string Markdown, string Rich)[]
        {
            ("10", "Tiskárna v kanceláři", "Úvod k tiskárně.\n## Nastavení **tiskárny**\nZapni tiskarnu.\n```\n## Není nadpis\n```\n## Kontakty\nVolej C# tým, C++ a cpp, c sharp.\n## Kontakty\nDruhý seznam ::secret{secretId=\"abc\"} heslo.\n~~~~\n## Taky není\n~~~\n## Pořád ne\n~~~~\n## [Odkaz](https://x.cz) a ![obrázek](a.png) ##\n",
                Rich(Heading("Nastavení tiskárny", "nastaveni"), Heading("Kontakty", "kontakty"), Heading("Kontakty", "bad id!"), nested, Heading("Odkaz a obrázek", "odkaz"))),
            ("11", "Kalendář API", "Popis API.\r\n## Endpoint\r\nMetoda getFreeBusy a PageRevisionStore, my_func.name, HTTPServerError.\r\n",
                Rich(Paragraph("Popis API."), openApi, Heading("Endpoint", "endpoint"))),
            ("12", "Postup obnovy zálohy", "## Obnova\nObnov zálohu.\n\t## Ne nadpis s tabulátorem\n   ## Odsazený nadpis\n#Bez mezery\n###### Šestý ###\n```\nneuzavřený blok\n## uvnitř",
                Rich(Heading("Obnova", "obnova"), Heading("Odsazený nadpis", null), Heading("Šestý", "sesty"))),
            ("13", "Prázdná", "", "{}"),
            ("14", "Importovaná stránka", "Text z Wiki.js s tiskárnami.", Rich(Paragraph("Text z Wiki.js s tiskárnami."))),
            ("15", "Jen koncept", "## Rozepsáno\nNic.", Rich(Heading("Rozepsáno", "rozepsano"))),
        };
        var sql = new System.Text.StringBuilder($"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','owner','Owner',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','Prostor','book-open','blue'),('{Other}','Jiný','book-open','red');
            """);
        foreach (var (id, title, markdown, rich) in pages)
        {
            var page = Id(id);
            sql.Append(CultureInfo.InvariantCulture, $"""
                INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{page}','{Space}','{Owner}',3);
                INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
                  ('{Id(id + "1")}','{page}',1,'{Sql("Starý " + title)}','{Sql(rich)}','{Sql(markdown)}','{Owner}','2026-01-01T00:00:00Z','2026-01-02T00:00:00Z','web'),
                  ('{Id(id + "2")}','{page}',2,'{Sql(title.ToUpperInvariant() + "!")}','{Sql(rich)}','{Sql(markdown)}','{Owner}','2026-01-02T00:00:00Z','2026-01-03T00:00:00Z','web'),
                  ('{Id(id + "3")}','{page}',3,'{Sql(title)}','{Sql(rich)}','{Sql(markdown + "\nKoncept navíc.")}','{Owner}','2026-01-03T00:00:00Z',NULL,'web');

                """);
            if (id != "15")
            {
                sql.Append(CultureInfo.InvariantCulture, $"""
                    INSERT INTO "PagePublication"("Id","PageId","RevisionId","PublisherId","ActiveFrom","ActiveTo","Channel") VALUES
                      (gen_random_uuid(),'{page}','{Id(id + "1")}','{Owner}','2026-01-01T00:00:00Z','2026-01-02T00:00:00Z','web'),
                      (gen_random_uuid(),'{page}','{Id(id + "2")}','{Owner}','2026-01-02T00:00:00Z',NULL,'web');
                    UPDATE "Page" SET "PublishedRevisionId"='{Id(id + "2")}' WHERE "Id"='{page}';

                    """);
            }
        }
        sql.Append(CultureInfo.InvariantCulture, $"""
            UPDATE "Page" SET "SpaceId"='{Other}' WHERE "Id"='{Id("12")}';
            INSERT INTO "WikiImportSource"("Id","SourceKey") VALUES('{Id("90")}','wikijs');
            INSERT INTO "WikiImportPage"("Id","SourceId","SourcePath","PageId") VALUES
              ('{Id("91")}','{Id("90")}','navody/tiskarny/importovana-stranka','{Id("14")}'),
              ('{Id("92")}','{Id("90")}','zz/nepublikovana-cesta','{Id("14")}');
            INSERT INTO "WikiImportCheckpoint"("Id","ImportPageId","RevisionId","SourceSha256","ActiveFrom") VALUES
              ('{Id("93")}','{Id("91")}','{Id("141")}',repeat('a',64),'2026-01-01T00:00:00Z'),
              ('{Id("94")}','{Id("92")}','{Id("143")}',repeat('b',64),'2026-01-03T00:00:00Z');
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder")
              VALUES('{Id("80")}','{Space}','Plán tiskáren',1,now(),'{Owner}',now(),false,1);
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Text","Version","IsDeleted","SearchText") VALUES
              ('{Id("81")}','{Id("80")}','note',0,0,100,100,2,'Druhá','1',false,'Druhá poznámka o HTTPServerError'),
              ('{Id("82")}','{Id("80")}','note',0,0,100,100,1,'První','1',false,'První poznámka: tiskárna v 1. patře'),
              ('{Id("83")}','{Id("80")}','note',0,0,100,100,3,'Smazaná','1',true,'Smazaná');
            INSERT INTO "Comment"("Id","ObjectId","AuthorId","Body","CreatedAt") VALUES
              ('{Id("70")}','{Id("10")}','{Owner}','Viz [návod](https://wiki/x) k tiskárně a C# kódu.',now());
            SELECT atlas_refresh_board_search('{Id("80")}');
            SELECT atlas_refresh_comment_search('{Id("70")}');
            """);
        await ExecuteAsync(connectionString, sql.ToString());

        static string Id(string suffix) => "00000000-0000-0000-0000-" + suffix.PadLeft(12, '0');
        static string Sql(string value) => value.Replace("'", "''", StringComparison.Ordinal);
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
