using Npgsql;
using Atlas.Server.Features.Search.Domain;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class SearchPostgresFactAttribute : FactAttribute
{
    public SearchPostgresFactAttribute()
    {
        // The same rule as PostgreSqlFact: on CI a missing connection fails instead of skipping.
        Skip = PostgreSqlFactAttribute.SkipReason(Environment.GetEnvironmentVariable("CI"),
            new[] { "ATLAS_TEST_DATABASE", "ATLAS_TEST_CONNECTION" }.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)));
    }
}

public sealed class SearchPostgresTests
{
    [SearchPostgresFact]
    public async Task Published_search_handles_accents_typos_ranking_and_immediate_acl_changes()
    {
        // An empty schema in its own database (TestDatabases): the test applies the early migrations itself.
        var schema = "atlas_search_" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(await Platform.TestDatabases.CreateEmptyAsync(schema));
        try
        {
            settings.SearchPath = schema + ",public";
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync();
            foreach (var migration in new[] { "001_initial.sql", "002_search.sql" })
            {
                await using var stream = typeof(AtlasDatabase).Assembly.GetManifestResourceStream("Atlas.Server.Persistence.Migrations." + migration)!;
                using var text = new StreamReader(stream);
                await Execute(connection, await text.ReadToEndAsync());
            }
            var actor = Guid.NewGuid();
            var allowed = Guid.NewGuid();
            var restricted = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO "UserAccount" VALUES (@actor,'reader','Reader',false,true,'stamp');
                INSERT INTO "Space" VALUES (@allowed,'Allowed','book','blue'),(@restricted,'Restricted','book','green');
                INSERT INTO "SpaceGrant" ("Id","SpaceId","UserId","Role") VALUES (gen_random_uuid(),@allowed,@actor,1);
                """, connection))
            {
                seed.Parameters.AddWithValue("actor", actor); seed.Parameters.AddWithValue("allowed", allowed); seed.Parameters.AddWithValue("restricted", restricted);
                await seed.ExecuteNonQueryAsync();
            }
            var exact = await AddPage(connection, actor, allowed, "Přístupy", "Účty a role.", true);
            await AddPage(connection, actor, allowed, "Přístupy do systémů", "Přihlášení pomocí OIDC.", true);
            var authentication = await AddPage(connection, actor, allowed, "Autentizace", "Bezpečné přihlášení.", true);
            await AddPage(connection, actor, restricted, "Přístupy tajné", "Tajná hesla.", true);
            await AddPage(connection, actor, allowed, "Přístupy rozpracované", "Dosud nepublikovaný draft.", false);

            await ApplyRemaining(connection, "001_initial.sql", "002_search.sql");
            var hits = await Find(connection, actor, "pristupy");
            Assert.Equal(exact, hits[0]);
            Assert.Equal(2, hits.Count);
            Assert.Contains(exact, await Find(connection, actor, "pristupi"));
            Assert.Contains(authentication, await Find(connection, actor, "autentikace"));
            Assert.Empty(await Find(connection, actor, "tajne"));
            Assert.Empty(await Find(connection, actor, "rozpracovane"));

            await Execute(connection, """UPDATE "SpaceGrant" SET "ActiveTo" = now() WHERE "ActiveTo" IS NULL""");
            Assert.Empty(await Find(connection, actor, "pristupy"));
            Assert.Equal(3, (await Find(connection, actor, "pristupy", true)).Count);
        }
        finally
        {
            await Platform.TestDatabases.DropAsync(settings.ConnectionString);
        }
    }

    [SearchPostgresFact]
    public async Task Technical_language_aliases_reindex_existing_content_and_preserve_distinct_results()
    {
        // An empty schema in its own database (TestDatabases): the test applies the early migrations itself.
        var schema = "atlas_technical_" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(await Platform.TestDatabases.CreateEmptyAsync(schema));
        try
        {
            settings.SearchPath = schema + ",public";
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync();
            async Task Migrate(string name)
            {
                await using var stream = typeof(AtlasDatabase).Assembly.GetManifestResourceStream("Atlas.Server.Persistence.Migrations." + name)!;
                using var reader = new StreamReader(stream);
                await Execute(connection, await reader.ReadToEndAsync());
            }
            await Migrate("001_initial.sql");
            await Migrate("002_search.sql");
            var actor = Guid.NewGuid(); var space = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO "UserAccount" VALUES (@actor,'reader','Reader',false,true,'stamp');
                INSERT INTO "Space" VALUES (@space,'Development','code','blue');
                INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","Role") VALUES (gen_random_uuid(),@space,@actor,1);
                """, connection))
            {
                seed.Parameters.AddWithValue("actor", actor); seed.Parameters.AddWithValue("space", space);
                await seed.ExecuteNonQueryAsync();
            }
            // These rows use the old punctuation-losing indexer, so 005 must rebuild them.
            var csharp = await AddPage(connection, actor, space, "C# handbook", "Úvod do jazyka.", true);
            var cpp = await AddPage(connection, actor, space, "C++ handbook", "Správa paměti.", true);
            await Migrate("005_search_technical_identifiers.sql");
            await ApplyRemaining(connection, "001_initial.sql", "002_search.sql", "005_search_technical_identifiers.sql");
            Assert.Contains(csharp, await Find(connection, actor, "C#"));
            Assert.DoesNotContain(cpp, await Find(connection, actor, "C#"));
            Assert.Contains(cpp, await Find(connection, actor, "C++"));
            Assert.DoesNotContain(csharp, await Find(connection, actor, "C++"));
            Assert.Contains(csharp, await Find(connection, actor, "c sharp"));
            Assert.Contains(csharp, await Find(connection, actor, "csharp"));
            Assert.Contains(cpp, await Find(connection, actor, "cpp"));
            // New publications are written by the code since 2026110935 (SearchDocuments: AddPage with a channel) and include aliases in body text.
            var bodyOnly = await AddPage(connection, actor, space, "Typový systém", "Pravidla typů v C# a .NET.", true, ContentChannels.Web);
            Assert.Contains(bodyOnly, await Find(connection, actor, "C#"));
            Assert.DoesNotContain(bodyOnly, await Find(connection, actor, "C++"));
            foreach (var sample in new[] { "C#", "C++", "c sharp", "cpp", "C++/CLI a C# na .NET", "C++20 a C#12", "abc# abc++ cppcheck", "PŘÍSTUPY uživatelů", "OpenID / .NET", "\" OR 1=1 --" })
            {
                await using var normalize = new NpgsqlCommand("SELECT " + SearchSqlText.Normalize("@input"), connection);
                normalize.Parameters.AddWithValue("input", sample);
                Assert.Equal(SearchText.Normalize(sample), await normalize.ExecuteScalarAsync());
            }
            foreach (var word in new[] { "smlouva", "smlouvou", "smlouvami", "tiskarnach", "kartami", "system", "kola", "forum", "abcde", "uzivatele", "x" })
            {
                await using var stem = new NpgsqlCommand("SELECT " + SearchSqlText.Stem("CAST(@word AS text)"), connection);
                stem.Parameters.AddWithValue("word", word);
                Assert.Equal(SearchText.Stem(word), await stem.ExecuteScalarAsync());
            }
            await Execute(connection, """UPDATE "SpaceGrant" SET "ActiveTo"=now() WHERE "ActiveTo" IS NULL""");
            Assert.Empty(await Find(connection, actor, "C#"));
        }
        finally { await Platform.TestDatabases.DropAsync(settings.ConnectionString); }
    }

    private static async Task<List<Guid>> Find(NpgsqlConnection connection, Guid actor, string query, bool admin = false)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        if (admin)
        {
            await using var grantAdmin = new NpgsqlCommand("UPDATE \"UserAccount\" SET \"IsWikiAdmin\"=true WHERE \"Id\"=@id",connection,transaction);
            grantAdmin.Parameters.AddWithValue("id",actor);
            await grantAdmin.ExecuteNonQueryAsync();
        }
        await using (var threshold = new NpgsqlCommand("SET LOCAL pg_trgm.word_similarity_threshold = 0.45", connection, transaction)) await threshold.ExecuteNonQueryAsync();
        var normalized = SearchText.Normalize(query);
        // The test runs the same query text as production.
        await using var command = new NpgsqlCommand("SELECT * FROM (" + Atlas.Server.Features.Search.SearchQueries.Published + ") moved", connection, transaction);
        command.Parameters.AddWithValue("query", normalized); command.Parameters.AddWithValue("prefix", normalized + "%");
        command.Parameters.AddWithValue("fts", SearchText.PrefixQuery(normalized)); command.Parameters.AddWithValue("fuzzy", normalized.Length >= 3);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("stamp", "stamp");
        command.Parameters.AddWithValue("phrase", false);
        command.Parameters.AddWithValue("space", NpgsqlTypes.NpgsqlDbType.Uuid, DBNull.Value);
        command.Parameters.AddWithValue("skip", 0); command.Parameters.AddWithValue("take", 21);
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        await transaction.CommitAsync();
        return ids;
    }

    private static async Task ApplyRemaining(NpgsqlConnection connection, params string[] alreadyApplied)
    {
        const string prefix = "Atlas.Server.Persistence.Migrations.";
        // Every runner (AtlasMigrations, the schema script) creates the journal before the first migration; 2026111140 reads it.
        await Execute(connection, AtlasMigrations.JournalSql);
        foreach (var resource in typeof(AtlasDatabase).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal))
        {
            if (alreadyApplied.Contains(resource[prefix.Length..], StringComparer.Ordinal))
            {
                continue;
            }
            await using var stream = typeof(AtlasDatabase).Assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            await Execute(connection, await reader.ReadToEndAsync());
        }
    }

    /// <param name="channel">The revision channel, once migration 2026100610 made it required; the earlier schema has no such column.</param>
    private static async Task<Guid> AddPage(NpgsqlConnection connection, Guid actor, Guid space, string title, string body, bool published, string? channel = null)
    {
        var id = Guid.NewGuid(); var revision = Guid.NewGuid();
        var revisionInsert = channel is null
            ? """INSERT INTO "PageRevision" VALUES (@revision,@id,1,@title,'{}',@body,@actor,now(),NULL);"""
            : """INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES (@revision,@id,1,@title,'{}',@body,@actor,now(),@channel);""";
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES (@id,@space,@actor,1);
            {revisionInsert}
            UPDATE "Page" SET "PublishedRevisionId" = CASE WHEN @published THEN @revision ELSE NULL END WHERE "Id" = @id;
            {(channel is null ? "" : Atlas.Server.Features.Search.SearchDocuments.RebuildAllSql)}
            """, connection);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("revision", revision); command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("space", space); command.Parameters.AddWithValue("title", title); command.Parameters.AddWithValue("body", body); command.Parameters.AddWithValue("published", published);
        if (channel is not null) command.Parameters.AddWithValue("channel", channel);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
