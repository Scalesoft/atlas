using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Spaces;

/// <summary>Migration 2026102975 over the rows an installation already has: pages and spaces, live and trashed, stay unpurged, and the
/// purges before it (an ObjectPurgeEvent) join the access history at the time they happened (SEC1-4).</summary>
public sealed class PermanentDeleteMigrationTests
{
    private const long Version = 2026102975;

    [PostgreSqlFact]
    public async Task Existing_pages_and_spaces_stay_unpurged_and_an_earlier_purge_joins_the_access_history()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_permanent_delete_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, Version);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','admin','Admin',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','Live','book-open','blue');
            INSERT INTO "Space"("Id","Name","Icon","Color","TrashedAt","TrashedById","TrashBatchId")
              VALUES('00000000-0000-0000-0000-00000000000b','Trashed','book-open','blue',now(),'00000000-0000-0000-0000-000000000001',gen_random_uuid());
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version","SortOrder")
              VALUES('00000000-0000-0000-0000-0000000000a1','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1,1024);
            INSERT INTO "ObjectPurgeEvent"("Id","ObjectId","TypeId","Scope","SpaceId","Title","PurgedById","PurgedAt")
              VALUES('00000000-0000-0000-0000-0000000000e1',gen_random_uuid(),'atlas.contact','wiki',NULL,'Jana','00000000-0000-0000-0000-000000000001','2026-10-29T10:00:00Z');
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "Space" WHERE "PurgedAt" IS NOT NULL"""));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "Page" WHERE "PurgedAt" IS NOT NULL"""));
        Assert.Equal("object.purged|Jana|atlas.contact|2026-10-29 10:00", await ScalarAsync(schema.ConnectionString, """
            SELECT v."Action" || '|' || v."ObjectTitle" || '|' || v."ObjectTypeId" || '|' || to_char(v."OccurredAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI')
            FROM "AccessAuditView" v WHERE v."ObjectPurgeEventId" = '00000000-0000-0000-0000-0000000000e1'
            """));
        // Only a trashed page or space can be purged.
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString,
            """UPDATE "Page" SET "PurgedAt" = now() WHERE "Id" = '00000000-0000-0000-0000-0000000000a1'"""));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString,
            """UPDATE "Space" SET "PurgedAt" = now() WHERE "Id" = '00000000-0000-0000-0000-00000000000a'"""));
        await ExecuteAsync(schema.ConnectionString, """UPDATE "Space" SET "PurgedAt" = now() WHERE "Id" = '00000000-0000-0000-0000-00000000000b'""");
    }

    /// <summary>Migration 2026103011 over a page purged before it (PER3-1, PER3-4): the index rows the old refresh wrote for its
    /// emptied revisions and its change summary go, a live page keeps both, and a later write to the tombstone indexes nothing.</summary>
    [PostgreSqlFact]
    public async Task A_page_purged_before_keeps_no_index_rows_and_no_change_summary()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_purged_leftovers_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026103011);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','admin','Admin',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','Live','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version","SortOrder","IsTrashed","TrashedAt","TrashedById","TrashBatchId")
              VALUES('00000000-0000-0000-0000-0000000000a1','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1,1024,
                true,now(),'00000000-0000-0000-0000-000000000001',gen_random_uuid()),
                ('00000000-0000-0000-0000-0000000000a2','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1,2048,false,NULL,NULL,NULL);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-0000000000a1',1,'','{"type":"doc","content":[]}','','00000000-0000-0000-0000-000000000001',now(),'web'),
              ('00000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-0000000000a2',1,'Živá','{"type":"doc","content":[]}','','00000000-0000-0000-0000-000000000001',now(),'web');
            INSERT INTO "PagePublication"("Id","PageId","RevisionId","PublisherId","ActiveFrom","Channel","ChangeSummary") VALUES
              (gen_random_uuid(),'00000000-0000-0000-0000-0000000000a1','00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-000000000001',now(),'web','Tajný popis'),
              (gen_random_uuid(),'00000000-0000-0000-0000-0000000000a2','00000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-000000000001',now(),'web','Popis');
            UPDATE "Page" SET "PurgedAt" = now() WHERE "Id" = '00000000-0000-0000-0000-0000000000a1';
            """);
        Assert.Equal(2L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "ProjectorIndexedRevision" """));

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("00000000-0000-0000-0000-0000000000a2", (await ScalarAsync(schema.ConnectionString,
            """SELECT CAST(string_agg(CAST("PageId" AS text), ',') AS text) FROM "ProjectorIndexedRevision" """))?.ToString());
        Assert.Equal("Popis", await ScalarAsync(schema.ConnectionString, """SELECT string_agg("ChangeSummary", ',') FROM "PagePublication" """));
        await ExecuteAsync(schema.ConnectionString, """
            UPDATE "PageRevision" SET "ContentJson" = '{"type":"doc","content":[{"type":"paragraph"}]}' WHERE "Id" = '00000000-0000-0000-0000-0000000000b1'
            """);
        Assert.Equal(1L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "ProjectorIndexedRevision" """));
    }

    /// <summary>Applies the embedded scripts below a version, with their journal, as the previous release left the schema.</summary>
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
