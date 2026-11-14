using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Notifications;

/// <summary>Migration 2026102960 over the pages an installation already has: their published mentions count as told, so the upgrade and
/// the next publication tell nobody of them; a draft's mention and an unknown account do not count. Migration 2026102970 then drops
/// the SQL reader of those mentions and keeps what it recorded.</summary>
public sealed class NotificationsMigrationTests
{
    private const long Version = 2026102960;
    private const string Writer = "00000000-0000-0000-0000-000000000001", Reader = "00000000-0000-0000-0000-000000000002";

    [PostgreSqlFact]
    public async Task Existing_published_mentions_are_told_already_and_the_inbox_starts_empty()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_inbox_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, Version);
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES
              ('{Writer}','writer','Writer',false,true,'stamp'),('{Reader}','reader','Reader',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES
              ('00000000-0000-0000-0000-0000000000a1','00000000-0000-0000-0000-00000000000a','{Writer}',1),
              ('00000000-0000-0000-0000-0000000000a2','00000000-0000-0000-0000-00000000000a','{Writer}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-0000000000a1',1,'Published','{Rich(Reader, "00000000-0000-0000-0000-0000000000ff")}','x','{Writer}',now(),'web'),
              ('00000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-0000000000a2',1,'Draft','{Rich(Reader)}','x','{Writer}',now(),'web');
            UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-0000000000b1' WHERE "Id"='00000000-0000-0000-0000-0000000000a1';
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal($"00000000-0000-0000-0000-0000000000a1:{Reader}",
            await ScalarAsync(schema.ConnectionString, """SELECT string_agg("PageId" || ':' || "UserId", ',') FROM "PageMentionNotice" """));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "Notification" """));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString,
            "SELECT count(*) FROM pg_proc WHERE proname IN ('atlas_page_account_mentions','atlas_rebuild_page_mention_notices')"));
    }

    private static string Rich(params string[] accounts) => """{"format":"scalesoft-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"contactInline","attrs":{"data":{"version":1,"contacts":["""
        + string.Join(",", accounts.Select(a => $$$"""{"name":"X","internalUser":{"id":"{{{a}}}"}}""")) + "]}}}]}]}}";

    private static async Task ApplyBeforeAsync(string connectionString, long version)
    {
        await ExecuteAsync(connectionString, "CREATE TABLE \"SchemaVersion\" (\"Version\" bigint PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: long.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < version).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await ExecuteAsync(connectionString, await reader.ReadToEndAsync());
            await ExecuteAsync(connectionString, $"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
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
