using Atlas.Server.Features.ContentExport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class FluentMigrationTests
{
    /// <summary>A merge of two lines can bring two migrations of one version (3.5.1 and 3.6 both took 2026110550): the server then never starts.</summary>
    [Fact]
    public void Every_migration_has_its_own_version()
    {
        var duplicates = typeof(AtlasDatabase).Assembly.GetTypes()
            .SelectMany(type => type.GetCustomAttributes(typeof(FluentMigrator.MigrationAttribute), false).Cast<FluentMigrator.MigrationAttribute>()
                .Select(migration => (migration.Version, type.Name)))
            .GroupBy(migration => migration.Version).Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(migration => migration.Name))}");
        Assert.Empty(duplicates);
    }

    [PostgreSqlFact]
    public async Task Page_decoration_palette_upgrade_preserves_legacy_rows_and_validates_new_choices()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < 2026092901).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await db.ExecuteAsync(await reader.ReadToEndAsync());
            await db.ExecuteAsync($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','legacy-writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color")
              VALUES('00000000-0000-0000-0000-000000000002','Legacy space','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              VALUES('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "PageDecoration"("PageId","RevisionId","Icon","Color")
              VALUES('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000004','folder','blue');
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        Assert.Equal("folder|blue", await db.ScalarAsync("SELECT \"Icon\" || '|' || \"Color\" FROM \"PageDecoration\""));
        await db.ExecuteAsync("UPDATE \"PageDecoration\" SET \"Icon\"='plug', \"Color\"='teal-2'");
        Assert.Equal("plug|teal-2", await db.ScalarAsync("SELECT \"Icon\" || '|' || \"Color\" FROM \"PageDecoration\""));
        // Icon pack (2026101141): the application holds the names; the database keeps their shape, 40 characters wide.
        await db.ExecuteAsync("UPDATE \"PageDecoration\" SET \"Icon\"='triangles-centerline-dashed-horizontal'");
        Assert.Equal("triangles-centerline-dashed-horizontal", await db.ScalarAsync("SELECT \"Icon\" FROM \"PageDecoration\""));
        foreach (var malformed in new[] { "Folder", "<svg>" })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync($"UPDATE \"PageDecoration\" SET \"Icon\"='{malformed}'"));
            Assert.Equal("CK_PageDecoration_Selection", refused.ConstraintName);
        }
    }

    [PostgreSqlFact]
    public async Task Working_search_migration_backfills_existing_current_revisions_without_changing_history()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal)).Order())
        {
            var version = int.Parse(resource.Split('.')[^2].Split('_')[0]);
            if (version > 25)
            {
                continue;
            }
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            await db.ExecuteAsync(await reader.ReadToEndAsync());
            await db.ExecuteAsync($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({version})");
        }
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','migration-writer','Writer',false,true,'migration-stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-000000000002','Migration','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom")
              VALUES('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000003',1,'Existing working title','{}','Existing body','00000000-0000-0000-0000-000000000001','2026-01-01T00:00:00Z');
            """);
        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        Assert.Equal("existing working title", await db.ScalarAsync("SELECT \"NormalizedTitle\" FROM \"WorkingSearchDocument\""));
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), await db.ScalarAsync("SELECT \"ChangedAt\" FROM \"WorkingSearchDocument\""));
        Assert.Equal(1L, await db.ScalarAsync("SELECT COUNT(*) FROM \"PageRevision\""));
        // Pinned spaces (2026092840): an existing space stays in everyone's sidebar, its current revision agrees.
        Assert.Equal("Default", await db.ScalarAsync("SELECT \"PinPolicy\" FROM \"Space\""));
        Assert.Equal("Default", await db.ScalarAsync("SELECT \"PinPolicy\" FROM \"SpaceMetadataRevision\" WHERE \"ActiveTo\" IS NULL"));
        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        Assert.Equal(1L, await db.ScalarAsync("SELECT COUNT(*) FROM \"WorkingSearchDocument\""));
        await db.ExecuteAsync("DELETE FROM \"PageContribution\"; DELETE FROM \"PageRevision\"");
        Assert.Equal(0L, await db.ScalarAsync("SELECT COUNT(*) FROM \"WorkingSearchDocument\""));
    }

    [PostgreSqlFact]
    public async Task Page_link_migration_backfills_published_revisions_only()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < 2026100420).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await db.ExecuteAsync(await reader.ReadToEndAsync());
            await db.ExecuteAsync($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
        // Page 3 publishes revision 4 (a pageReference to 7, a link to itself); its working revision 5 links to 8 instead.
        // Page 6 was never published and links to 7.
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','migration-writer','Writer',false,true,'migration-stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-000000000002','Migration','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES
              ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001',2),
              ('00000000-0000-0000-0000-000000000006','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo") VALUES
              ('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000003',1,'Published',
               '{"document":{"type":"doc","content":[{"type":"pageReference","attrs":{"id":"00000000-0000-0000-0000-000000000007","title":"T","url":"#/pages/00000000-0000-0000-0000-000000000007"}},{"type":"text","text":"self","marks":[{"type":"link","attrs":{"href":"#/pages/00000000-0000-0000-0000-000000000003"}}]}]}}',
               'published','00000000-0000-0000-0000-000000000001','2026-01-01T00:00:00Z','2026-01-02T00:00:00Z'),
              ('00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000003',2,'Working',
               '{"document":{"type":"doc","content":[{"type":"text","text":"w","marks":[{"type":"link","attrs":{"href":"#/pages/00000000-0000-0000-0000-000000000008"}}]}]}}',
               'working','00000000-0000-0000-0000-000000000001','2026-01-02T00:00:00Z',NULL),
              ('00000000-0000-0000-0000-000000000009','00000000-0000-0000-0000-000000000006',1,'Draft',
               '{"document":{"type":"doc","content":[{"type":"pageReference","attrs":{"id":"00000000-0000-0000-0000-000000000007","title":"T","url":"#/pages/00000000-0000-0000-0000-000000000007"}}]}}',
               'draft','00000000-0000-0000-0000-000000000001','2026-01-01T00:00:00Z',NULL);
            UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-000000000004' WHERE "Id"='00000000-0000-0000-0000-000000000003';
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        const string links = """SELECT string_agg("SourcePageId"||'>'||"TargetPageId"||'@'||"SourceRevisionId",',') FROM "PageLink" """;
        const string expected = "00000000-0000-0000-0000-000000000003>00000000-0000-0000-0000-000000000007@00000000-0000-0000-0000-000000000004";
        Assert.Equal(expected, await db.ScalarAsync(links));
        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        // Migration 2026110871: the rows stay, the trigger and the functions go, the rebuild job is queued (PageDerivedRowsMigrationTests runs it).
        Assert.Equal(expected, await db.ScalarAsync(links));
        Assert.Equal(0L, await db.ScalarAsync("""SELECT count(*) FROM pg_trigger WHERE tgname = 'TR_Page_PageLink'"""));
        Assert.Equal(1L, await db.ScalarAsync("""SELECT count(*) FROM "JobRun" WHERE "JobId" = 'pages.links-rebuild' AND "State" = 'queued'"""));
    }

    [PostgreSqlFact]
    public async Task Pgvector_migration_creates_the_extension_once_and_supports_cosine_hnsw()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM \"SchemaVersion\" WHERE \"Version\"=2026100520"));
        Assert.Equal("public", await db.ScalarAsync("SELECT n.nspname::text FROM pg_extension e JOIN pg_namespace n ON n.oid=e.extnamespace WHERE e.extname='vector'"));
        Assert.True(Convert.ToBoolean(await db.ScalarAsync("SELECT string_to_array(extversion, '.')::int[] >= ARRAY[0,8,1] FROM pg_extension WHERE extname='vector'")));
        // What V2/V3 rely on: a cosine HNSW index and iterative scans, accepted inside one transaction.
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var sql in new[]
                 {
                     "CREATE TEMP TABLE probe (v vector(3)) ON COMMIT DROP",
                     "INSERT INTO probe VALUES ('[1,0,0]'), ('[0,1,0]')",
                     "CREATE INDEX ON probe USING hnsw (v vector_cosine_ops)",
                     "SET LOCAL hnsw.iterative_scan = relaxed_order",
                     "SET LOCAL enable_seqscan = off"
                 })
        {
            await using var step = new NpgsqlCommand(sql, connection, transaction);
            await step.ExecuteNonQueryAsync();
        }
        await using var nearest = new NpgsqlCommand("SELECT v::text FROM probe ORDER BY v <=> '[1,0.1,0]' LIMIT 1", connection, transaction);
        Assert.Equal("[1,0,0]", await nearest.ExecuteScalarAsync());
        await transaction.RollbackAsync();
    }

    /// <summary>Upgrade = nothing (AI-24): no backfill, no generation, so neither the migration nor a later publication enqueues
    /// anything. Once an administrator builds an index, a publication's delivery enqueues the page for it (no trigger since 2026110873).</summary>
    [PostgreSqlFact]
    public async Task Knowledge_index_migration_creates_no_tasks_without_a_generation()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < 2026100540).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await db.ExecuteAsync(await reader.ReadToEndAsync());
            await db.ExecuteAsync($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','migration-admin','Admin',true,true,'migration-stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-000000000002','Migration','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES
              ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001',2);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo") VALUES
              ('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000003',1,'First','{}','first',
               '00000000-0000-0000-0000-000000000001','2026-01-01T00:00:00Z','2026-01-02T00:00:00Z'),
              ('00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000003',2,'Second','{}','second',
               '00000000-0000-0000-0000-000000000001','2026-01-02T00:00:00Z',NULL);
            UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-000000000004' WHERE "Id"='00000000-0000-0000-0000-000000000003';
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        const string counts = """SELECT (SELECT count(*) FROM "IndexTask") || '/' || (SELECT count(*) FROM "IndexGeneration") || '/' || (SELECT count(*) FROM "KnowledgePage")""";
        Assert.Equal("0/0/0", await db.ScalarAsync(counts));
        await db.ExecuteAsync("""UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-000000000005' WHERE "Id"='00000000-0000-0000-0000-000000000003'""");
        Assert.Equal("0/0/0", await db.ScalarAsync(counts));

        await db.ExecuteAsync("""
            INSERT INTO "AiProfileRevision"("Id","Kind","Enabled","Provider","Location","BaseUrl","Model","TimeoutSeconds","MaxConcurrency","Dimensions","Fingerprint","CreatedById","CreatedAt")
              VALUES('00000000-0000-0000-0000-000000000006','embeddings',true,'openai-compatible','company','http://lmstudio.example/v1','bge-m3',30,4,1024,
                'fingerprint','00000000-0000-0000-0000-000000000001',now());
            INSERT INTO "IndexGeneration"("Id","State","ProfileRevisionId","Fingerprint","ChunkingVersion","Dimensions","CreatedById","CreatedAt","BoundaryAt","Version")
              VALUES('00000000-0000-0000-0000-000000000007','Building','00000000-0000-0000-0000-000000000006','fingerprint','headings-1800@1',1024,
                '00000000-0000-0000-0000-000000000001',now(),now(),now());
            UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-000000000004' WHERE "Id"='00000000-0000-0000-0000-000000000003';
            """);
        // Migration 2026110873: the tasks are enqueued by code (IndexTaskRevision, SaveSpaceAiSettingsHandler), no longer by a trigger.
        Assert.Equal("0/1/0", await db.ScalarAsync(counts));
        Assert.Equal(0L, await db.ScalarAsync("""
            SELECT count(*) FROM pg_trigger WHERE tgname IN ('TR_Page_IndexTask','TR_SpaceAiSetting_IndexTask')
            """));
        Assert.Equal(0L, await db.ScalarAsync("""SELECT count(*) FROM pg_proc WHERE proname IN ('atlas_index_task_trigger','atlas_space_ai_index_trigger','atlas_enqueue_index_sync')"""));
    }
    /// <summary>Plan phase 3 Q1: conversations start empty on an upgrade (no backfill, AI-24) and the existing wiki is untouched.</summary>
    [PostgreSqlFact]
    public async Task Ai_conversation_migration_is_empty_on_upgrade()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < 2026100700).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await db.ExecuteAsync(await reader.ReadToEndAsync());
            await db.ExecuteAsync($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','migration-admin','Admin',true,true,'migration-stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-000000000002','Migration','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES
              ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000003',1,'First','{}','first',
               '00000000-0000-0000-0000-000000000001','2026-01-01T00:00:00Z','web');
            UPDATE "Page" SET "PublishedRevisionId"='00000000-0000-0000-0000-000000000004' WHERE "Id"='00000000-0000-0000-0000-000000000003';
            INSERT INTO "AiProfileRevision"("Id","Kind","Enabled","Provider","Location","BaseUrl","Model","TimeoutSeconds","MaxConcurrency","CreatedById","CreatedAt")
              VALUES('00000000-0000-0000-0000-000000000005','chat',true,'openai-compatible','company','http://lmstudio.example/v1','gemma-4',120,2,
                '00000000-0000-0000-0000-000000000001',now());
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);

        Assert.Equal("0/0/0", await db.ScalarAsync("""
            SELECT (SELECT count(*) FROM "AiConversation") || '/' || (SELECT count(*) FROM "AiMessage") || '/' || (SELECT count(*) FROM "AiSourceDependency")
            """));
        // atlas_ai_dependency_status, which this migration created, read nothing at runtime and went with migration 2026110935.
        Assert.Equal(0L, await db.ScalarAsync("""SELECT count(*) FROM pg_proc WHERE proname = 'atlas_ai_dependency_status'"""));
        Assert.Equal("00000000-0000-0000-0000-000000000004", (await db.ScalarAsync("""
            SELECT "PublishedRevisionId"::text FROM "Page" WHERE "Id"='00000000-0000-0000-0000-000000000003'
            """))?.ToString());
    }

    /// <summary>The source of a space image (2026101142): a row with a file was an upload, a row without one was a removal, which
    /// is now the automatic pick; history is backfilled with the current rows. The new constraints keep the source, the file and
    /// the catalog key in step.</summary>
    [PostgreSqlFact]
    public async Task Space_cover_source_migration_backfills_uploads_and_removals_and_enforces_the_shape()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < 2026101142).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await db.ExecuteAsync(await reader.ReadToEndAsync());
            await db.ExecuteAsync($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
        // Space 2 has an untinted upload. Space 3 had an upload (row 5) and then lost it (row 6).
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','migration-admin','Admin',true,true,'migration-stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES
              ('00000000-0000-0000-0000-000000000002','Uploaded','book-open','blue'),
              ('00000000-0000-0000-0000-000000000003','Removed','book-open','blue');
            INSERT INTO "SpaceCover"("Id","SpaceId","FileId","MimeType","Size","Sha256","Tinted","ChangedBy","ActiveFrom","ActiveTo") VALUES
              ('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-00000000000a',
               'image/png',68,repeat('a',64),false,'00000000-0000-0000-0000-000000000001','2026-01-01T00:00:00Z',NULL),
              ('00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-00000000000b',
               'image/jpeg',14,repeat('b',64),true,'00000000-0000-0000-0000-000000000001','2026-01-01T00:00:00Z','2026-01-02T00:00:00Z'),
              ('00000000-0000-0000-0000-000000000006','00000000-0000-0000-0000-000000000003',NULL,
               NULL,NULL,NULL,true,'00000000-0000-0000-0000-000000000001','2026-01-02T00:00:00Z',NULL);
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);

        Assert.Equal("Upload:false,Upload:true,Auto:true", await db.ScalarAsync("""
            SELECT string_agg("Source" || ':' || "Tinted", ',' ORDER BY "Id") FROM "SpaceCover"
            """));
        // One statement per constraint: a closed row of space 2 without a file, in a period of its own.
        static string Row(string source, string catalogKey) => $"""
            INSERT INTO "SpaceCover"("Id","SpaceId","Source","CatalogKey","ActiveFrom","ActiveTo")
              VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000002','{source}',{catalogKey},'2025-01-01T00:00:00Z','2025-01-02T00:00:00Z')
            """;
        Assert.Equal("CK_SpaceCover_Shape", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(Row("Catalog", "NULL")))).ConstraintName);
        Assert.Equal("CK_SpaceCover_Source", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(Row("Drawing", "NULL")))).ConstraintName);
        Assert.Equal("CK_SpaceCover_CatalogKey", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(Row("Catalog", "'It Backup'")))).ConstraintName);
        await db.ExecuteAsync(Row("Catalog", "'it-backup'"));
    }

    /// <summary>The refused live snapshot (2026101250): an existing live document has no refusal, and a refusal comes with its
    /// sequence within the document's range.</summary>
    [PostgreSqlFact]
    public async Task Collaboration_refusal_migration_leaves_live_documents_without_a_refusal()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < 2026101250).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await db.ExecuteAsync(await reader.ReadToEndAsync());
            await db.ExecuteAsync($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','migration-admin','Admin',true,true,'migration-stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-000000000002','Migration','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES
              ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              ('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000003',1,'First','{}','first',
               '00000000-0000-0000-0000-000000000001','2026-01-01T00:00:00Z','web');
            INSERT INTO "PageCollaboration"("PageId","Generation","BaseRevisionId","Sequence","SnapshotSequence")
              VALUES('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000004',3,1);
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);

        Assert.Equal("3/1/none", await db.ScalarAsync("""
            SELECT "Sequence" || '/' || "SnapshotSequence" || '/' || coalesce("RefusedSequence"::text || "Refusal", 'none') FROM "PageCollaboration"
            """));
        static string Refuse(string sequence, string refusal) =>
            $"UPDATE \"PageCollaboration\" SET \"RefusedSequence\"={sequence},\"Refusal\"={refusal}";
        foreach (var update in new[] { Refuse("2", "NULL"), Refuse("NULL", "'{}'"), Refuse("4", "'{}'"), Refuse("0", "'{}'") })
        {
            Assert.Equal("CK_PageCollaboration_Refusal", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(update))).ConstraintName);
        }
        await db.ExecuteAsync(Refuse("3", "'{}'"));
    }

    /// <summary>Cover details of an automatic PDF (2026101300): an export configured before keeps stating everything, and the
    /// column takes only a list of known details.</summary>
    [PostgreSqlFact]
    public async Task Pdf_cover_details_migration_keeps_existing_exports_stating_everything()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < 2026101300).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await db.ExecuteAsync(await reader.ReadToEndAsync());
            await db.ExecuteAsync($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','migration-admin','Admin',true,true,'migration-stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-000000000002','Migration','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES
              ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "StorageProfile"("Id","Name","Destination","Username","Domain")
              VALUES('00000000-0000-0000-0000-000000000004','Dokumentace','/srv/pdf','','');
            INSERT INTO "PagePdfExport"("PageId","StorageProfileId","OwnerId","Folder","FileName","SourceOrigin")
              VALUES('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000001','guide','document.pdf','https://atlas.test/');
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);

        Assert.Equal(PdfCoverDetails.Default, await db.ScalarAsync("""SELECT "CoverDetails" FROM "PagePdfExport" """));
        await db.ExecuteAsync("""UPDATE "PagePdfExport" SET "CoverDetails"='revision,revisionDate'""");
        await db.ExecuteAsync("""UPDATE "PagePdfExport" SET "CoverDetails"=''""");
        Assert.Equal("CK_PagePdfExport_CoverDetails", (await Assert.ThrowsAsync<PostgresException>(() =>
            db.ExecuteAsync("""UPDATE "PagePdfExport" SET "CoverDetails"='revision,secret'"""))).ConstraintName);
    }

    /// <summary>Shared PDF folder (2026101640): an export that already wrote its file keeps it as its last file, a second page may use
    /// the same folder under another name, and the same name (case-insensitive) is still refused.</summary>
    [PostgreSqlFact]
    public async Task Pdf_export_shared_folder_migration_keeps_existing_files_and_allows_other_names_in_the_folder()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < 2026101640).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await db.ExecuteAsync(await reader.ReadToEndAsync());
            await db.ExecuteAsync($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','migration-admin','Admin',true,true,'migration-stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-000000000002','Migration','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES
              ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001',1),
              ('00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001',1),
              ('00000000-0000-0000-0000-000000000006','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "StorageProfile"("Id","Name","Destination","Username","Domain")
              VALUES('00000000-0000-0000-0000-000000000004','Dokumentace','/srv/pdf','','');
            INSERT INTO "PagePdfExport"("PageId","StorageProfileId","OwnerId","Folder","FileName","SourceOrigin","LastExportedAt") VALUES
              ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000001','guide','Guide.pdf','https://atlas.test/',now()),
              ('00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000001','other','Other.pdf','https://atlas.test/',NULL);
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);

        Assert.Equal("guide/Guide.pdf|none", await db.ScalarAsync("""
            SELECT string_agg(coalesce("LastFolder" || '/' || "LastFileName", 'none'), '|' ORDER BY "PageId") FROM "PagePdfExport"
              WHERE "LastStorageProfileId" IS NOT DISTINCT FROM (CASE WHEN "LastExportedAt" IS NULL THEN NULL ELSE "StorageProfileId" END)
            """));
        await db.ExecuteAsync("""
            INSERT INTO "PagePdfExport"("PageId","StorageProfileId","OwnerId","Folder","FileName","SourceOrigin")
              VALUES('00000000-0000-0000-0000-000000000006','00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000001','Guide','Second.pdf','https://atlas.test/')
            """);
        Assert.Equal("UX_PagePdfExport_Target", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("""
            UPDATE "PagePdfExport" SET "FileName"='GUIDE.PDF' WHERE "PageId"='00000000-0000-0000-0000-000000000006'
            """))).ConstraintName);
    }

    /// <summary>The "AI s nástroji" right (2026101605, D20): an existing group grant gets tools, a personal row inherits, the
    /// rows stay immutable and the upgrade invents no audit event.</summary>
    [PostgreSqlFact]
    public async Task Ai_tools_policy_migration_gives_existing_grants_tools_and_keeps_personal_rows_inheriting()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < 2026101605).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await db.ExecuteAsync(await reader.ReadToEndAsync());
            await db.ExecuteAsync($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','migration-admin','Admin',true,true,'migration-stamp');
            INSERT INTO "UserGroup"("Id","Name") VALUES('00000000-0000-0000-0000-000000000002','AI pilot');
            INSERT INTO "GroupAiPolicy"("Id","GroupId","Enabled","ChatAnswerDailyLimit","ChangedById","ActiveFrom")
              VALUES('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002',true,5,
                '00000000-0000-0000-0000-000000000001','2026-01-01T00:00:00Z');
            INSERT INTO "UserAiPolicy"("Id","UserId","Enabled","ChangedById","ActiveFrom")
              VALUES('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000001',true,
                '00000000-0000-0000-0000-000000000001','2026-01-01T00:00:00Z');
            """);
        var audit = await db.ScalarAsync("""SELECT count(*) FROM "AccessAuditEvent" """);

        // Up to the migrations that move these guards into the application (2026110944, 2026110945; AuditInCodeMigrationTests).
        await Events.OutboxTests.ApplyBeforeAsync(db.ConnectionString, 2026110941, from: 2026101605);

        Assert.Equal("true/5/null", await db.ScalarAsync("""
            SELECT "ToolsEnabled" || '/' || "ChatAnswerDailyLimit" || '/' || coalesce("AgentDailyLimit"::text,'null') FROM "GroupAiPolicy"
            """));
        Assert.Equal("null/null", await db.ScalarAsync("""
            SELECT coalesce("ToolsEnabled"::text,'null') || '/' || coalesce("AgentDailyLimit"::text,'null') FROM "UserAiPolicy"
            """));
        Assert.Equal(audit, await db.ScalarAsync("""SELECT count(*) FROM "AccessAuditEvent" """));
        Assert.Contains("immutable", (await Assert.ThrowsAsync<PostgresException>(() =>
            db.ExecuteAsync("""UPDATE "GroupAiPolicy" SET "ToolsEnabled"=false"""))).MessageText);
        Assert.Contains("immutable", (await Assert.ThrowsAsync<PostgresException>(() =>
            db.ExecuteAsync("""UPDATE "UserAiPolicy" SET "ToolsEnabled"=false"""))).MessageText);
        Assert.Equal("CK_GroupAiPolicy_AgentLimit", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("""
            INSERT INTO "GroupAiPolicy"("Id","GroupId","Enabled","AgentDailyLimit","ChangedById","ActiveFrom")
              VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000002',true,10001,'00000000-0000-0000-0000-000000000001','2025-01-01T00:00:00Z')
            """))).ConstraintName);
    }

    /// <summary>The content channel assistant (2026101610): existing web, mcp and legacy rows stay valid and readable through the audit
    /// view; assistant is accepted only without a token, web never with one, and an audited write without a token is the assistant's.</summary>
    [PostgreSqlFact]
    public async Task Assistant_channel_migration_keeps_existing_rows_and_accepts_assistant_only_without_a_token()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < 2026101610).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await db.ExecuteAsync(await reader.ReadToEndAsync());
            await db.ExecuteAsync($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
        const string user = "00000000-0000-0000-0000-000000000001";
        const string page = "00000000-0000-0000-0000-000000000003";
        const string token = "00000000-0000-0000-0000-000000000006";
        await db.ExecuteAsync($"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('{user}','migration-admin','Admin',true,true,'migration-stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-000000000002','Migration','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{page}','00000000-0000-0000-0000-000000000002','{user}',3);
            INSERT INTO "PersonalApiToken"("Id","UserId","Name","TokenHash","Prefix","SecurityStamp","ActiveFrom","ExpiresAt")
              VALUES('{token}','{user}','Claude Code','{new string('A', 64)}','atl_test','stamp','2026-01-01T00:00:00Z','2027-01-01T00:00:00Z');
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel","ApiTokenId") VALUES
              ('00000000-0000-0000-0000-000000000011','{page}',1,'Legacy','{"{}"}','legacy','{user}','2026-01-01T00:00:00Z','2026-01-02T00:00:00Z','legacy',NULL),
              ('00000000-0000-0000-0000-000000000012','{page}',2,'Web','{"{}"}','web','{user}','2026-01-02T00:00:00Z','2026-01-03T00:00:00Z','web',NULL),
              ('00000000-0000-0000-0000-000000000013','{page}',3,'Mcp','{"{}"}','mcp','{user}','2026-01-03T00:00:00Z',NULL,'mcp','{token}');
            INSERT INTO "PagePublication"("Id","PageId","RevisionId","PublisherId","ActiveFrom","Channel","ApiTokenId")
              VALUES('00000000-0000-0000-0000-000000000021','{page}','00000000-0000-0000-0000-000000000013','{user}','2026-01-03T00:00:00Z','mcp','{token}');
            INSERT INTO "PageAccessEvent"("PageId","RevisionId","ActorId","Action","Channel","ApiTokenId")
              VALUES('{page}','00000000-0000-0000-0000-000000000013','{user}','page.opened','mcp','{token}');
            INSERT INTO "ApiActivity"("ActorId","TokenId","Channel","Operation","PageId","Outcome","ResultVersion")
              VALUES('{user}','{token}','mcp','atlas_update_section','{page}','ok',3);
            """);

        // Up to the migration that moves this audit into the code (2026110944, tested in AuditInCodeMigrationTests).
        await Events.OutboxTests.ApplyBeforeAsync(db.ConnectionString, 2026110941, from: 2026101610);

        Assert.Equal("legacy:,mcp:t,web:", await db.ScalarAsync("""
            SELECT string_agg("Channel" || ':' || CASE WHEN "ApiTokenId" IS NULL THEN '' ELSE 't' END, ',' ORDER BY "Channel") FROM "PageRevision"
            """));
        Assert.Equal("mcp.write:mcp:Claude Code", await db.ScalarAsync("""
            SELECT "Action" || ':' || "ApiChannel" || ':' || "ApiTokenName" FROM "AccessAuditView" WHERE "ApiActivityId" IS NOT NULL
            """));

        string Revision(int version, string channel, string tokenId) => $"""
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel","ApiTokenId")
              VALUES(gen_random_uuid(),'{page}',{version},'New','{"{}"}','new','{user}','2026-02-0{version - 3}T00:00:00Z','{channel}',{tokenId})
            """;
        foreach (var refused in new[] { Revision(4, "assistant", $"'{token}'"), Revision(4, "web", $"'{token}'"), Revision(4, "mcp", "NULL") })
        {
            Assert.Equal("CK_PageRevision_Channel", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(refused))).ConstraintName);
        }
        await db.ExecuteAsync($"""UPDATE "PageRevision" SET "ActiveTo"='2026-02-01T00:00:00Z' WHERE "ActiveTo" IS NULL""");
        await db.ExecuteAsync(Revision(4, "assistant", "NULL"));
        await db.ExecuteAsync($"""
            UPDATE "PagePublication" SET "ActiveTo"='2026-02-01T00:00:00Z';
            INSERT INTO "PagePublication"("Id","PageId","RevisionId","PublisherId","ActiveFrom","Channel")
              SELECT gen_random_uuid(),'{page}',"Id",'{user}','2026-02-01T00:00:00Z','assistant' FROM "PageRevision" WHERE "Version"=4;
            INSERT INTO "PageAccessEvent"("PageId","RevisionId","ActorId","Action","Channel")
              SELECT '{page}',"Id",'{user}','page.opened','assistant' FROM "PageRevision" WHERE "Version"=4;
            """);
        Assert.Equal("CK_PageAccessEvent_Channel", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync($"""
            INSERT INTO "PageAccessEvent"("PageId","RevisionId","ActorId","Action","Channel","ApiTokenId")
              VALUES('{page}','00000000-0000-0000-0000-000000000013','{user}','page.opened','assistant','{token}')
            """))).ConstraintName);

        string Activity(string channel, string tokenId, string operation) => $"""
            INSERT INTO "ApiActivity"("ActorId","TokenId","Channel","Operation","PageId","Outcome") VALUES('{user}',{tokenId},'{channel}','{operation}','{page}','ok')
            """;
        foreach (var refused in new[] { Activity("assistant", $"'{token}'", "atlas_set_tags"), Activity("mcp", "NULL", "atlas_set_tags"), Activity("api", "NULL", "page.save") })
        {
            Assert.Equal("CK_ApiActivity_Channel", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(refused))).ConstraintName);
        }
        await db.ExecuteAsync(Activity("assistant", "NULL", "atlas_set_tags"));
        await db.ExecuteAsync(Activity("assistant", "NULL", "atlas_publish"));
        Assert.Equal("mcp.write:mcp:Claude Code,assistant.write:assistant:-,assistant.publish:assistant:-", await db.ScalarAsync("""
            SELECT string_agg("Action" || ':' || "ApiChannel" || ':' || coalesce("ApiTokenName",'-'), ',' ORDER BY "Id") FROM "AccessAuditView" WHERE "ApiActivityId" IS NOT NULL
            """));
    }

    /// <summary>The steps of an agent turn (2026101620): existing messages keep their answers and read without steps; only an answered
    /// message may carry steps, and only as an array.</summary>
    [PostgreSqlFact]
    public async Task Agent_steps_migration_keeps_existing_messages_without_steps_and_allows_steps_only_on_answers()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < 2026101620).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await db.ExecuteAsync(await reader.ReadToEndAsync());
            await db.ExecuteAsync($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
        const string user = "00000000-0000-0000-0000-000000000001";
        const string conversation = "00000000-0000-0000-0000-000000000003";
        const string answer = """{"answerable":true,"sentences":[{"text":"Věta.","sources":[]}]}""";
        await db.ExecuteAsync($"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('{user}','migration-admin','Admin',true,true,'migration-stamp');
            INSERT INTO "AiProfileRevision"("Id","Kind","Enabled","Provider","Location","BaseUrl","Model","TimeoutSeconds","MaxConcurrency","CreatedById","CreatedAt")
              VALUES('00000000-0000-0000-0000-000000000002','chat',true,'openai-compatible','company','http://lmstudio.example/v1','gemma-4',120,2,'{user}',now());
            INSERT INTO "AiConversation"("Id","OwnerId","Origin","Listed","ChatProfileRevisionId","ChatCompatibility","CreatedAt","LastActivityAt","Version")
              VALUES('{conversation}','{user}','assistant',true,'00000000-0000-0000-0000-000000000002','{new string('c', 64)}',now(),now(),2);
            INSERT INTO "AiMessage"("Id","ConversationId","Ordinal","Role","State","Task","Question","ScopeKind","Answer","CreatedAt","CompletedAt") VALUES
              ('00000000-0000-0000-0000-000000000011','{conversation}',1,'user',NULL,NULL,'Otázka?','wiki',NULL,now(),NULL),
              ('00000000-0000-0000-0000-000000000012','{conversation}',2,'assistant','answered','chat.answer',NULL,NULL,
                '{answer}',now(),now()),
              ('00000000-0000-0000-0000-000000000013','{conversation}',3,'user',NULL,NULL,'Další?','wiki',NULL,now(),NULL),
              ('00000000-0000-0000-0000-000000000014','{conversation}',4,'assistant','failed','chat.answer',NULL,NULL,NULL,now(),now());
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);

        Assert.Equal("4/0/Věta.", await db.ScalarAsync("""
            SELECT count(*) || '/' || count("Steps") || '/' || max("Answer"->'sentences'->0->>'text') FROM "AiMessage"
            """));
        await db.ExecuteAsync("""UPDATE "AiMessage" SET "Steps"='[{"tool":"atlas_search","ok":true}]' WHERE "Ordinal"=2""");
        foreach (var refused in new[] { """UPDATE "AiMessage" SET "Steps"='[]' WHERE "Ordinal"=4""", """UPDATE "AiMessage" SET "Steps"='{}' WHERE "Ordinal"=2""" })
        {
            Assert.Equal("CK_AiMessage_Steps", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(refused))).ConstraintName);
        }
    }

    /// <summary>Proposals of agent writes (2026101630): existing conversations and messages stay as they were and have no proposal; a
    /// proposal belongs to one answer, one per conversation may be pending, a decided one carries its time, only an executed one its
    /// results, and it goes with its conversation.</summary>
    [PostgreSqlFact]
    public async Task Tool_proposal_migration_keeps_existing_messages_and_enforces_one_pending_proposal_per_conversation()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version < 2026101630).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await db.ExecuteAsync(await reader.ReadToEndAsync());
            await db.ExecuteAsync($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
        const string user = "00000000-0000-0000-0000-000000000001";
        const string conversation = "00000000-0000-0000-0000-000000000003";
        const string answer = """{"answerable":true,"sentences":[{"text":"Věta.","sources":[]}]}""";
        await db.ExecuteAsync($$"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('{{user}}','migration-admin','Admin',true,true,'migration-stamp');
            INSERT INTO "AiProfileRevision"("Id","Kind","Enabled","Provider","Location","BaseUrl","Model","TimeoutSeconds","MaxConcurrency","CreatedById","CreatedAt")
              VALUES('00000000-0000-0000-0000-000000000002','chat',true,'openai-compatible','company','http://lmstudio.example/v1','gemma-4',120,2,'{{user}}',now());
            INSERT INTO "AiConversation"("Id","OwnerId","Origin","Listed","ChatProfileRevisionId","ChatCompatibility","CreatedAt","LastActivityAt","Version")
              VALUES('{{conversation}}','{{user}}','assistant',true,'00000000-0000-0000-0000-000000000002','{{new string('c', 64)}}',now(),now(),2);
            INSERT INTO "AiMessage"("Id","ConversationId","Ordinal","Role","State","Task","Question","ScopeKind","Answer","CreatedAt","CompletedAt","Steps") VALUES
              ('00000000-0000-0000-0000-000000000011','{{conversation}}',1,'user',NULL,NULL,'Otázka?','wiki',NULL,now(),NULL,NULL),
              ('00000000-0000-0000-0000-000000000012','{{conversation}}',2,'assistant','answered','assistant.agent',NULL,NULL,'{{answer}}',now(),now(),
                '[{"tool":"atlas_search","ok":true}]'),
              ('00000000-0000-0000-0000-000000000013','{{conversation}}',3,'user',NULL,NULL,'Další?','wiki',NULL,now(),NULL,NULL),
              ('00000000-0000-0000-0000-000000000014','{{conversation}}',4,'assistant','answered','assistant.agent',NULL,NULL,'{{answer}}',now(),now(),NULL);
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);

        Assert.Equal("4/1/0", await db.ScalarAsync("""
            SELECT count(*) || '/' || count("Steps") || '/' || (SELECT count(*) FROM "AiToolProposal") FROM "AiMessage"
            """));
        const string items = """[{"index":0}]""";
        string Proposal(string id, string message, string state = "pending", string decided = "NULL", string results = "NULL")
            => $$"""
                INSERT INTO "AiToolProposal"("Id","MessageId","ConversationId","Tool","Arguments","Items","State","Results","CreatedAt","ExpiresAt","DecidedAt")
                VALUES('{{id}}','{{message}}','{{conversation}}','atlas_apply_labels','{"changes":[]}','{{items}}','{{state}}',{{results}},now(),now()+interval '15 minutes',{{decided}})
                """;
        await db.ExecuteAsync(Proposal("00000000-0000-0000-0000-000000000021", "00000000-0000-0000-0000-000000000012"));
        Assert.Equal("UX_AiToolProposal_Pending", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(
            Proposal("00000000-0000-0000-0000-000000000022", "00000000-0000-0000-0000-000000000014")))).ConstraintName);
        Assert.Equal("UX_AiToolProposal_Message", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(
            Proposal("00000000-0000-0000-0000-000000000022", "00000000-0000-0000-0000-000000000012", "declined", "now()")))).ConstraintName);
        foreach (var (state, decided, results, constraint) in new[]
                 {
                     ("declined", "NULL", "NULL", "CK_AiToolProposal_Decided"), ("applied", "now()", "NULL", "CK_AiToolProposal_Executed"),
                     ("declined", "now()", "'[]'", "CK_AiToolProposal_Executed"), ("done", "now()", "NULL", "CK_AiToolProposal_State"),
                 })
        {
            Assert.Equal(constraint, (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(
                Proposal("00000000-0000-0000-0000-000000000022", "00000000-0000-0000-0000-000000000014", state, decided, results)))).ConstraintName);
        }
        await db.ExecuteAsync(Proposal("00000000-0000-0000-0000-000000000022", "00000000-0000-0000-0000-000000000014", "applied", "now()",
            """'[{"index":0,"status":"applied"}]'"""));
        await db.ExecuteAsync($"""DELETE FROM "AiConversation" WHERE "Id"='{conversation}'""");
        Assert.Equal(0L, await db.ScalarAsync("""SELECT count(*) FROM "AiToolProposal" """));
    }

    /// <summary>A server without pgvector, or with an older one, stops the whole batch with the Czech upgrade procedure. The
    /// test server has pgvector, so a schema-local view placed before pg_catalog on the search path hides it.</summary>
    [PostgreSqlFact]
    public async Task Pgvector_migration_stops_with_the_upgrade_procedure_when_the_extension_is_missing_or_too_old()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        var hidden = db.WithSearchPathBeforeCatalog();
        await ExecuteAsync(hidden, "CREATE VIEW pg_available_extensions AS SELECT * FROM pg_catalog.pg_available_extensions WHERE name <> 'vector'");
        using (var database = new AtlasDatabase(Configuration(hidden), NullLogger<AtlasDatabase>.Instance))
        {
            var missing = await Assert.ThrowsAsync<ApiException>(() => database.InitializeAsync(CancellationToken.None));
            Assert.Equal(503, missing.Status);
            Assert.Contains("pgvector 0.8.1", missing.Message);
            Assert.Contains("Upgrade na 2.0", missing.Message);
            // Every request until a retry succeeds (the setup status page among them) names the requirement too.
            Assert.Equal(missing.Message, Assert.Throws<ApiException>(() => database.OpenSession()).Message);
        }
        Assert.Equal(DBNull.Value, await db.ScalarAsync("SELECT to_regclass(quote_ident(current_schema()) || '.\"SchemaVersion\"')::text"));
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM pg_catalog.pg_extension WHERE extname='vector'"));

        await ExecuteAsync(hidden, """
            DROP VIEW pg_available_extensions;
            CREATE VIEW pg_extension AS SELECT e.oid, e.extname, e.extowner, e.extnamespace, e.extrelocatable,
              CASE WHEN e.extname='vector' THEN '0.8.0' ELSE e.extversion END AS extversion, e.extconfig, e.extcondition
            FROM pg_catalog.pg_extension e;
            """);
        var old = await Assert.ThrowsAsync<ApiException>(() => InitializeAsync(hidden));
        Assert.Contains("ALTER EXTENSION vector UPDATE", old.Message);
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM pg_catalog.pg_extension WHERE extname='vector'"));

        static IConfiguration Configuration(string connectionString) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:atlas-db"] = connectionString }).Build();

        static async Task InitializeAsync(string connectionString)
        {
            using var database = new AtlasDatabase(Configuration(connectionString), NullLogger<AtlasDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
        }

        // On the shadowing search path: with the default one, an unqualified name resolves to pg_catalog first.
        static async Task ExecuteAsync(string connectionString, string sql)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>The first 2.x → 3 data migration (Atlas 3 boards spec §3): every existing page, trashed ones and pages of
    /// every space included, gets exactly one ContentResource row with its own space, and re-running the script adds nothing.</summary>
    [PostgreSqlFact]
    public async Task Content_resource_migration_registers_every_existing_page_once_with_its_space()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await ApplyBeforeAsync(db, 2026102600);
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','legacy-writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES
              ('00000000-0000-0000-0000-00000000000a','First','book-open','blue'),
              ('00000000-0000-0000-0000-00000000000b','Second','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES
              ('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1),
              ('00000000-0000-0000-0000-000000000012','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1),
              ('00000000-0000-0000-0000-000000000021','00000000-0000-0000-0000-00000000000b','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000011',1,'Old','{}','','00000000-0000-0000-0000-000000000001','2026-01-01T00:00:00Z','2026-02-01T00:00:00Z','web'),
              ('00000000-0000-0000-0000-000000000032','00000000-0000-0000-0000-000000000011',2,'New','{}','','00000000-0000-0000-0000-000000000001','2026-02-01T00:00:00Z',NULL,'web');
            UPDATE "Page" SET "IsTrashed"=true,"TrashedAt"=now(),"TrashedById"='00000000-0000-0000-0000-000000000001',"TrashBatchId"=gen_random_uuid()
              WHERE "Id"='00000000-0000-0000-0000-000000000012';
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        const string resources = """SELECT string_agg(right("Id"::text,2)||'@'||right("SpaceId"::text,1)||':'||"TypeId",',' ORDER BY "Id") FROM "ContentResource" """;
        const string expected = "11@a:atlas.page,12@a:atlas.page,21@b:atlas.page";
        Assert.Equal(expected, await db.ScalarAsync(resources));
        // A page is as old as its first revision; one without any (never saved) dates from the migration.
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            await db.ScalarAsync("""SELECT "CreatedAt" FROM "ContentResource" WHERE "Id"='00000000-0000-0000-0000-000000000011'"""));
        Assert.Equal(true, await db.ScalarAsync("""SELECT "CreatedAt" > now() - interval '1 hour' FROM "ContentResource" WHERE "Id"='00000000-0000-0000-0000-000000000021'"""));

        // The script itself is re-runnable (an operator replaying --database-script output, a retried batch): nothing doubles.
        await db.ExecuteAsync(await EmbeddedSqlAsync("2026102600_content_resource.sql"));
        Assert.Equal(expected, await db.ScalarAsync(resources));
        Assert.Equal(1L, await db.ScalarAsync("""SELECT count(*) FROM pg_trigger WHERE tgname='TR_Page_ContentResource' AND tgrelid='"Page"'::regclass"""));
    }

    /// <summary>The triggers keep ContentResource without any page path knowing it: a new page gets its row, a cross-space move
    /// moves it, a deleted page takes it along.</summary>
    [PostgreSqlFact]
    public async Task Content_resource_follows_page_insert_cross_space_move_and_delete()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        // Up to 2026110930, which moved what these triggers did to C# (ContentRowEffects).
        await ApplyBeforeAsync(db, 2026110930);
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES
              ('00000000-0000-0000-0000-00000000000a','First','book-open','blue'),
              ('00000000-0000-0000-0000-00000000000b','Second','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              VALUES('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1);
            """);
        const string row = """SELECT "TypeId"||'@'||right("SpaceId"::text,1) FROM "ContentResource" WHERE "Id"='00000000-0000-0000-0000-000000000011'""";
        Assert.Equal("atlas.page@a", await db.ScalarAsync(row));

        await db.ExecuteAsync("""UPDATE "Page" SET "SpaceId"='00000000-0000-0000-0000-00000000000b' WHERE "Id"='00000000-0000-0000-0000-000000000011'""");
        Assert.Equal("atlas.page@b", await db.ScalarAsync(row));
        // Other page updates (here a trash round trip) leave the row as it is.
        await db.ExecuteAsync("""UPDATE "Page" SET "Version"=2 WHERE "Id"='00000000-0000-0000-0000-000000000011'""");
        Assert.Equal("atlas.page@b", await db.ScalarAsync(row));

        // Pages are only trashed today; a hard delete still removes its identity. Its history rows are immutable, so the test
        // lifts their guards in its own database to get the page itself deleted.
        await db.ExecuteAsync("""
            ALTER TABLE "PageOrganizationRevision" DISABLE TRIGGER USER;
            ALTER TABLE "PageAccessModeRevision" DISABLE TRIGGER USER;
            DELETE FROM "PageOrganizationRevision" WHERE "PageId"='00000000-0000-0000-0000-000000000011';
            DELETE FROM "PageAccessModeRevision" WHERE "PageId"='00000000-0000-0000-0000-000000000011';
            DELETE FROM "Page" WHERE "Id"='00000000-0000-0000-0000-000000000011';
            """);
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM \"ContentResource\""));
    }

    /// <summary>The board tables arrive empty next to existing pages, which keep their resources; a board registers as
    /// atlas.board through its trigger, and the item constraints back the domain rules.</summary>
    [PostgreSqlFact]
    public async Task Board_migration_adds_empty_tables_and_registers_boards_as_content_resources()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await ApplyBeforeAsync(db, 2026102610);
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              VALUES('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1);
            """);
        // Up to 2026110930, which moved what these triggers did to C# (ContentRowEffects).
        await ApplyBeforeAsync(db, 2026110930, from: 2026102610);
        Assert.Equal(0L, await db.ScalarAsync("""SELECT (SELECT count(*) FROM "Board")+(SELECT count(*) FROM "BoardItem")+(SELECT count(*) FROM "BoardOperation")"""));
        Assert.Equal("atlas.page", await db.ScalarAsync("""SELECT string_agg("TypeId",',') FROM "ContentResource" """));

        await db.ExecuteAsync("""
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder")
              VALUES('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a','Plan',0,now(),'00000000-0000-0000-0000-000000000001',now(),false,1024);
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Version","IsDeleted") VALUES
              ('00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000b1','note',0,0,160,120,0,1,false);
            """);
        Assert.Equal("atlas.board@a", await db.ScalarAsync("""SELECT "TypeId"||'@'||right("SpaceId"::text,1) FROM "ContentResource" WHERE "Id"='00000000-0000-0000-0000-0000000000b1'"""));
        // A connector needs two different ends of the same board, a page card its target; NaN never passes the geometry check.
        foreach (var (values, constraint) in new[]
                 {
                     ("'connector',0,0,8,8,0,NULL,'00000000-0000-0000-0000-0000000000c1',NULL", "CK_BoardItem_Shape"),
                     ("'connector',0,0,8,8,0,NULL,'00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000c1'", "CK_BoardItem_Shape"),
                     ("'page',0,0,8,8,0,NULL,NULL,NULL", "CK_BoardItem_Shape"),
                     ("'note','NaN',0,8,8,0,NULL,NULL,NULL", "CK_BoardItem_Geometry")
                 })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync($"""
                INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","TargetResourceId","FromItemId","ToItemId","Version","IsDeleted")
                  VALUES(gen_random_uuid(),'00000000-0000-0000-0000-0000000000b1',{values},1,false)
                """));
            Assert.Equal(constraint, refused.ConstraintName);
        }
        await db.ExecuteAsync("""
            DELETE FROM "BoardItem";
            DELETE FROM "Board";
            """);
        Assert.Equal("atlas.page", await db.ScalarAsync("""SELECT string_agg("TypeId",',') FROM "ContentResource" """));
    }

    /// <summary>The interactions migration over the prototype's rows (interaction spec §4): notes get no props, page cards the card
    /// display, connectors an elbow route with an arrow at the end (and the other required keys' defaults), boards the default
    /// settings; the new kinds and colours pass the constraints, which still keep a target to page cards and ends to connectors,
    /// and props and settings are always objects.</summary>
    [PostgreSqlFact]
    public async Task Board_interactions_migration_gives_prototype_rows_their_defaults_and_admits_the_new_kinds_and_colours()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await ApplyBeforeAsync(db, 2026102620);
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              VALUES('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed")
              VALUES('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a','Plan',4,now(),'00000000-0000-0000-0000-000000000001',now(),false);
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Text","Color","TargetResourceId","FromItemId","ToItemId","Version","IsDeleted") VALUES
              ('00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000b1','note',0,0,160,120,0,'Hi','yellow',NULL,NULL,NULL,1,false),
              ('00000000-0000-0000-0000-0000000000c2','00000000-0000-0000-0000-0000000000b1','note',200,0,160,120,1,NULL,NULL,NULL,NULL,NULL,2,true),
              ('00000000-0000-0000-0000-0000000000c3','00000000-0000-0000-0000-0000000000b1','page',0,200,240,160,2,NULL,NULL,'00000000-0000-0000-0000-000000000011',NULL,NULL,3,false),
              ('00000000-0000-0000-0000-0000000000c4','00000000-0000-0000-0000-0000000000b1','connector',0,0,8,8,3,'depends',NULL,NULL,
                '00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000c3',4,false);
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        const string items = """SELECT string_agg(right("Id"::text,2)||'='||"Kind",',' ORDER BY "Id") FROM "BoardItem" """;
        Assert.Equal("c1=note,c2=note", await db.ScalarAsync(items + """WHERE "PropsJson"='{}' """));
        Assert.Equal("c3=page", await db.ScalarAsync(items + """WHERE "PropsJson"='{"display":"card"}' """));
        Assert.Equal("{}|4|Plan", await db.ScalarAsync("""SELECT "SettingsJson"::text||'|'||"Sequence"||'|'||"Title" FROM "Board" """));
        Assert.Equal("c4=connector", await db.ScalarAsync(items + """WHERE "PropsJson"='{"route":"elbow","arrowStart":"none","arrowEnd":"arrow","dash":"solid"}' """));
        // Everything else of the rows stays as it was.
        Assert.Equal("Hi|yellow|1,depends||4", await db.ScalarAsync("""
            SELECT string_agg(coalesce("Text",'')||'|'||coalesce("Color",'')||'|'||"Version",',' ORDER BY "Id") FROM "BoardItem"
              WHERE "Id" IN ('00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000c4')
            """));

        await db.ExecuteAsync("""
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Color","PropsJson","Version","IsDeleted") VALUES
              (gen_random_uuid(),'00000000-0000-0000-0000-0000000000b1','block',0,0,240,120,5,'teal','{"block":{"type":"paragraph"}}',5,false),
              (gen_random_uuid(),'00000000-0000-0000-0000-0000000000b1','text',0,0,240,40,6,'black','{"align":"left","fontSize":"m"}',5,false),
              (gen_random_uuid(),'00000000-0000-0000-0000-0000000000b1','frame',0,0,800,600,-1,'white','{}',5,false);
            """);
        foreach (var (kind, color, props, target, constraint) in new[]
                 {
                     ("block", "NULL", """'{"block":{"type":"paragraph"}}'""", "'00000000-0000-0000-0000-000000000011'", "CK_BoardItem_Shape"),
                     ("youtrack", "NULL", "'{}'", "NULL", "CK_BoardItem_Shape"),
                     ("shape", "'brown'", """'{"shape":"rect"}'""", "NULL", "CK_BoardItem_Color"),
                     ("note", "NULL", "'[]'", "NULL", "CK_BoardItem_Props")
                 })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync($"""
                INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Color","PropsJson","TargetResourceId","Version","IsDeleted")
                  VALUES(gen_random_uuid(),'00000000-0000-0000-0000-0000000000b1','{kind}',0,0,8,8,0,{color},{props},{target},1,false)
                """));
            Assert.Equal(constraint, refused.ConstraintName);
        }
        var settings = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("""UPDATE "Board" SET "SettingsJson"='null'"""));
        Assert.Equal("CK_Board_Settings", settings.ConstraintName);
    }

    /// <summary>Boards in the space tree (interaction spec §6) over the prototype's boards: every one goes to the end of its space's
    /// root in title order, a trashed one keeps a trash stamp of its own, and every board gets its search document from its items'
    /// texts (a page card has none of its own).</summary>
    [PostgreSqlFact]
    public async Task Boards_tree_migration_puts_prototype_boards_at_the_root_end_in_title_order_and_indexes_their_texts()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await ApplyBeforeAsync(db, 2026102640);
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES
              ('00000000-0000-0000-0000-00000000000a','First','book-open','blue'),
              ('00000000-0000-0000-0000-00000000000b','Second','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES
              ('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1),
              ('00000000-0000-0000-0000-000000000012','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a','Zeta',0,now(),'00000000-0000-0000-0000-000000000001',now(),false),
              ('00000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-00000000000a','Alpha',2,now(),'00000000-0000-0000-0000-000000000001',now(),false),
              ('00000000-0000-0000-0000-0000000000b3','00000000-0000-0000-0000-00000000000a','Old',0,now(),'00000000-0000-0000-0000-000000000001','2026-10-01T00:00:00Z',true),
              ('00000000-0000-0000-0000-0000000000b4','00000000-0000-0000-0000-00000000000b','Solo',0,now(),'00000000-0000-0000-0000-000000000001',now(),false);
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Text","TargetResourceId","FromItemId","ToItemId","Version","IsDeleted","PropsJson") VALUES
              ('00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000b2','note',0,0,160,120,0,'**Kickoff** with the customer',NULL,NULL,NULL,1,false,'{}'),
              ('00000000-0000-0000-0000-0000000000c2','00000000-0000-0000-0000-0000000000b2','page',0,0,240,160,1,NULL,'00000000-0000-0000-0000-000000000011',NULL,NULL,1,false,'{"display":"card"}'),
              ('00000000-0000-0000-0000-0000000000c3','00000000-0000-0000-0000-0000000000b2','connector',0,0,8,8,2,'depends on',NULL,
                '00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000c2',2,false,'{"arrowEnd":"arrow","arrowStart":"none","dash":"solid","route":"elbow"}'),
              ('00000000-0000-0000-0000-0000000000c4','00000000-0000-0000-0000-0000000000b2','note',0,0,160,120,3,'gone',NULL,NULL,NULL,2,true,'{}');
            UPDATE "Page" SET "SortOrder"=2048 WHERE "Id"='00000000-0000-0000-0000-000000000012';
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        // The root of First ends with its page at 2048; Alpha, Old and Zeta follow in title order, Solo starts Second.
        Assert.Equal("b2:3072:|b3:4096:|b1:5120:|b4:1024:", await db.ScalarAsync("""
            SELECT string_agg(right("Id"::text,2)||':'||"SortOrder"||':'||coalesce("ParentPageId"::text,''),'|' ORDER BY "SpaceId","SortOrder") FROM "Board"
            """));
        Assert.Equal("b3:true", await db.ScalarAsync("""
            SELECT string_agg(right("Id"::text,2)||':'||("TrashedAt"='2026-10-01T00:00:00Z' AND "TrashedById" IS NOT NULL AND "TrashBatchId" IS NOT NULL),',')
            FROM "Board" WHERE "IsTrashed"
            """));
        Assert.Equal("c1:**Kickoff** with the customer|c2:|c3:depends on|c4:gone", await db.ScalarAsync("""
            SELECT string_agg(right("Id"::text,2)||':'||coalesce("SearchText",''),'|' ORDER BY "Id") FROM "BoardItem"
            """));
        // Searchable by the title and by the texts of live items only, through the pages' lexemes (stems, prefixes).
        const string found = """SELECT string_agg(right("BoardId"::text,2),',' ORDER BY "BoardId") FROM "BoardSearchDocument" WHERE "Vector" @@ to_tsquery('simple',:q)""";
        Assert.Equal("b2", await db.ScalarAsync(found.Replace(":q", "'kickoff:* & customer:*'")));
        Assert.Equal("b2", await db.ScalarAsync(found.Replace(":q", "'alpha'")));
        Assert.Equal("b4", await db.ScalarAsync(found.Replace(":q", "'solo'")));
        Assert.Equal(DBNull.Value, await db.ScalarAsync(found.Replace(":q", "'gone'")));
    }

    /// <summary>Favorites and recent visits name any content resource: the existing page rows stay, a board's row is accepted,
    /// an id that is no content resource still is not.</summary>
    [PostgreSqlFact]
    public async Task Library_resources_migration_keeps_page_favorites_and_visits_and_admits_boards()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await ApplyBeforeAsync(db, 2026102650);
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              VALUES('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder")
              VALUES('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a','Plan',0,now(),'00000000-0000-0000-0000-000000000001',now(),false,1024);
            INSERT INTO "PageFavorite"("Id","UserId","PageId","ActiveFrom")
              VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011','2026-10-01T00:00:00Z');
            INSERT INTO "PageVisit"("Id","UserId","PageId","LastVisitedAt")
              VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011','2026-10-02T00:00:00Z');
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        Assert.Equal("11|11", await db.ScalarAsync("""SELECT (SELECT right("PageId"::text,2) FROM "PageFavorite")||'|'||(SELECT right("PageId"::text,2) FROM "PageVisit")"""));
        await db.ExecuteAsync("""
            INSERT INTO "PageFavorite"("Id","UserId","PageId","ActiveFrom")
              VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-0000000000b1',now());
            INSERT INTO "PageVisit"("Id","UserId","PageId","LastVisitedAt")
              VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-0000000000b1',now());
            """);
        foreach (var (table, column, constraint) in new[] { ("PageFavorite", "\"ActiveFrom\"", "FK_PageFavorite_Resource"), ("PageVisit", "\"LastVisitedAt\"", "FK_PageVisit_Resource") })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync($"""
                INSERT INTO "{table}"("Id","UserId","PageId",{column}) VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000001',gen_random_uuid(),now())
                """));
            Assert.Equal(constraint, refused.ConstraintName);
        }
    }

    /// <summary>Explicit membership over the boards' existing rows (objects spec §5.4): every live item lying fully inside a live
    /// frame of its board joins the smallest such frame, a frame inside a frame too; connectors, deleted items and what a deleted
    /// frame holds stay free; a chain deeper than four levels is cut every fifth level. The constraints keep a parent on the same
    /// board, away from connectors and from the item itself, and admit groups.</summary>
    [PostgreSqlFact]
    public async Task Board_groups_migration_derives_frame_membership_from_geometry_once()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await ApplyBeforeAsync(db, 2026102680);
        static string Item(string id, string board, string kind, int x, int y, int size, bool deleted = false) =>
            $"('00000000-0000-0000-0000-0000000000{id}','00000000-0000-0000-0000-0000000000{board}','{kind}',{x},{y},{size},{size},0,NULL,NULL,1,{(deleted ? "true" : "false")},"
            + (kind == "page" ? "'00000000-0000-0000-0000-000000000011'" : "NULL") + ")";
        // b1: frame f1 ⊃ frame f2 ⊃ note a1; a2 in f1 only; a3 outside; a4 deleted in f2; c1 a connector in f2; a5 in a deleted
        // frame; a page card in f1. b2: a frame over the same area, of another board. b3: six frames nested, a note in the last.
        string[] rows =
        [
            Item("f1", "b1", "frame", 0, 0, 1000), Item("f2", "b1", "frame", 100, 100, 400), Item("a1", "b1", "note", 150, 150, 100),
            Item("a2", "b1", "note", 600, 600, 100), Item("a3", "b1", "note", 2000, 2000, 100), Item("a4", "b1", "note", 160, 160, 100, deleted: true),
            Item("fd", "b1", "frame", 3000, 0, 500, deleted: true), Item("a5", "b1", "note", 3100, 100, 100), Item("a6", "b1", "page", 700, 100, 200),
            Item("f9", "b2", "frame", -10, -10, 2000), Item("b9", "b2", "note", 150, 150, 100),
            ..Enumerable.Range(0, 6).Select(level => Item($"d{level}", "b3", "frame", level * 10, level * 10, 1000 - level * 20)),
            Item("e1", "b3", "note", 100, 100, 50)
        ];
        await db.ExecuteAsync($$"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              VALUES('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a','One',3,now(),'00000000-0000-0000-0000-000000000001',now(),false,1024),
              ('00000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-00000000000a','Two',1,now(),'00000000-0000-0000-0000-000000000001',now(),false,2048),
              ('00000000-0000-0000-0000-0000000000b3','00000000-0000-0000-0000-00000000000a','Three',1,now(),'00000000-0000-0000-0000-000000000001',now(),false,3072);
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","FromItemId","ToItemId","Version","IsDeleted","TargetResourceId") VALUES
              {{string.Join(",\n  ", rows)}};
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","FromItemId","ToItemId","Version","IsDeleted","PropsJson") VALUES
              ('00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000b1','connector',200,200,8,8,0,
               '00000000-0000-0000-0000-0000000000a1','00000000-0000-0000-0000-0000000000a2',1,false,'{"arrowEnd":"arrow","arrowStart":"none","dash":"solid","route":"elbow"}');
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        Assert.Equal("a1:f2|a2:f1|a3:|a4:|a5:|a6:f1|b9:f9|c1:|d0:|d1:d0|d2:d1|d3:d2|d4:d3|d5:|e1:d5|f1:|f2:f1|f9:|fd:", await db.ScalarAsync("""
            SELECT string_agg(right("Id"::text,2)||':'||coalesce(right("ParentId"::text,2),''),'|' ORDER BY right("Id"::text,2)) FROM "BoardItem"
            """));
        // Nothing else of the rows changed.
        Assert.Equal("1:3", await db.ScalarAsync("""SELECT max("Version")||':'||(SELECT "Sequence" FROM "Board" WHERE "Title"='One') FROM "BoardItem" """));

        await db.ExecuteAsync("""
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Text","Color","Version","IsDeleted","ParentId","PropsJson")
              VALUES(gen_random_uuid(),'00000000-0000-0000-0000-0000000000b1','group',0,0,100,100,0,'Tým','teal',1,false,'00000000-0000-0000-0000-0000000000f1','{"collapsed":true}');
            """);
        foreach (var (id, parent, constraint) in new[]
                 {
                     ("c1", "f1", "CK_BoardItem_Parent"),
                     ("a3", "a3", "CK_BoardItem_Parent"),
                     ("a3", "f9", "FK_BoardItem_Parent")
                 })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync($"""
                UPDATE "BoardItem" SET "ParentId"='00000000-0000-0000-0000-0000000000{parent}' WHERE "Id"='00000000-0000-0000-0000-0000000000{id}'
                """));
            Assert.Equal(constraint, refused.ConstraintName);
        }
    }

    /// <summary>Object cards over existing rows (objects spec §5.1): the board's items stay as they are, and an object card is an
    /// item with a target and no ends, like a page card.</summary>
    [PostgreSqlFact]
    public async Task Board_object_cards_migration_keeps_existing_items_and_admits_object_cards_with_a_target()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await ApplyBeforeAsync(db, 2026102750);
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a','One',2,now(),'00000000-0000-0000-0000-000000000001',now(),false,1024);
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Version","IsDeleted","TargetResourceId","Text") VALUES
              ('00000000-0000-0000-0000-0000000000a1','00000000-0000-0000-0000-0000000000b1','note',0,0,100,100,0,1,false,NULL,'Hello'),
              ('00000000-0000-0000-0000-0000000000a2','00000000-0000-0000-0000-0000000000b1','page',0,0,100,100,1,2,false,'00000000-0000-0000-0000-000000000011',NULL);
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        Assert.Equal("a1:note:1:Hello|a2:page:2:", await db.ScalarAsync("""
            SELECT string_agg(right("Id"::text,2)||':'||"Kind"||':'||"Version"||':'||coalesce("Text",''),'|' ORDER BY "Id") FROM "BoardItem"
            """));

        await db.ExecuteAsync("""
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Version","IsDeleted","TargetResourceId","PropsJson")
              VALUES('00000000-0000-0000-0000-0000000000a3','00000000-0000-0000-0000-0000000000b1','object',0,0,280,160,2,3,false,
                     '00000000-0000-0000-0000-0000000000c7','{"objectId":"00000000-0000-0000-0000-0000000000c7","typeId":"atlas.contact","view":"card"}');
            """);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("""
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Version","IsDeleted")
              VALUES(gen_random_uuid(),'00000000-0000-0000-0000-0000000000b1','object',0,0,280,160,3,3,false);
            """));
        Assert.Equal("CK_BoardItem_Shape", refused.ConstraintName);
    }

    /// <summary>Lists derived from the relations over existing rows (2026110185, P15): the board's items stay as they are, and a
    /// `links` item is one without a target or ends, like a note.</summary>
    [PostgreSqlFact]
    public async Task Board_links_migration_keeps_existing_items_and_admits_links_without_a_target()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await ApplyBeforeAsync(db, 2026110185);
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a','One',2,now(),'00000000-0000-0000-0000-000000000001',now(),false,1024);
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Version","IsDeleted","TargetResourceId","Text") VALUES
              ('00000000-0000-0000-0000-0000000000a1','00000000-0000-0000-0000-0000000000b1','note',0,0,100,100,0,1,false,NULL,'Hello'),
              ('00000000-0000-0000-0000-0000000000a2','00000000-0000-0000-0000-0000000000b1','object',0,0,100,100,1,2,false,'00000000-0000-0000-0000-000000000011',NULL);
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        Assert.Equal("a1:note:1:Hello|a2:object:2:", await db.ScalarAsync("""
            SELECT string_agg(right("Id"::text,2)||':'||"Kind"||':'||"Version"||':'||coalesce("Text",''),'|' ORDER BY "Id") FROM "BoardItem"
            """));

        await db.ExecuteAsync("""
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Version","IsDeleted","PropsJson")
              VALUES('00000000-0000-0000-0000-0000000000a3','00000000-0000-0000-0000-0000000000b1','links',0,0,340,320,2,3,false,'{"show":"people"}');
            """);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("""
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Version","IsDeleted","TargetResourceId")
              VALUES(gen_random_uuid(),'00000000-0000-0000-0000-0000000000b1','links',0,0,340,320,3,3,false,'00000000-0000-0000-0000-000000000011');
            """));
        Assert.Equal("CK_BoardItem_Shape", refused.ConstraintName);
    }

    /// <summary>The object envelope over existing rows (Atlas 3 F1 plan §2.1): every page and board becomes a space-scoped object
    /// with its version (the page's save version, the board's sequence), its author, its last change and its trash time; the
    /// triggers keep those as pages and boards change; the scope rules hold for objects the platform stores; the change log
    /// arrives empty.</summary>
    [PostgreSqlFact]
    public async Task Object_envelope_migration_gives_existing_pages_and_boards_their_scope_version_and_lifecycle()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await ApplyBeforeAsync(db, 2026102700);
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES
              ('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp'),
              ('00000000-0000-0000-0000-000000000002','author','Author',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES
              ('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',3),
              ('00000000-0000-0000-0000-000000000012','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000002',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              ('00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000011',1,'Old','{}','','00000000-0000-0000-0000-000000000001','2026-01-01T00:00:00Z','2026-02-01T00:00:00Z','web'),
              ('00000000-0000-0000-0000-000000000032','00000000-0000-0000-0000-000000000011',2,'New','{}','','00000000-0000-0000-0000-000000000001','2026-02-01T00:00:00Z',NULL,'web');
            UPDATE "Page" SET "IsTrashed"=true,"TrashedAt"='2026-03-01T00:00:00Z',"TrashedById"='00000000-0000-0000-0000-000000000001',"TrashBatchId"=gen_random_uuid()
              WHERE "Id"='00000000-0000-0000-0000-000000000012';
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a','Plan',5,'2026-04-01T00:00:00Z','00000000-0000-0000-0000-000000000002','2026-04-02T00:00:00Z',false,1024);
            """);

        // Up to 2026110930, which moved what these triggers did to C# (ContentRowEffects).
        await ApplyBeforeAsync(db, 2026110930, from: 2026102700);
        const string envelope = """
            SELECT string_agg(right("Id"::text,2)||':'||"Scope"||'@'||right("SpaceId"::text,1)||' v'||"Version"||' by '||right("CreatedBy"::text,1)
                ||' owner '||COALESCE(right("OwnerId"::text,1),'-')||' updated '||to_char("UpdatedAt" AT TIME ZONE 'UTC','MM-DD')
                ||' trashed '||COALESCE(to_char("TrashedAt" AT TIME ZONE 'UTC','MM-DD'),'-'),', ' ORDER BY "Id")
            FROM "ContentResource"
            """;
        // A page is changed when its last revision was written, a page without revisions when it was registered (today).
        var today = (string)(await db.ScalarAsync("SELECT to_char(now() AT TIME ZONE 'UTC','MM-DD')"))!;
        Assert.Equal($"11:space@a v3 by 1 owner - updated 02-01 trashed -, 12:space@a v1 by 2 owner - updated {today} trashed 03-01, b1:space@a v5 by 2 owner - updated 04-02 trashed -",
            await db.ScalarAsync(envelope));
        Assert.Equal(0L, await db.ScalarAsync("""SELECT count(*) FROM "ObjectChange" """));

        // The triggers keep the envelope: a save moves the version and the change time, trash and restore the trash time, a
        // batch the board's version; a new page registers with its owner as its author.
        await db.ExecuteAsync("""
            UPDATE "Page" SET "Version"=4 WHERE "Id"='00000000-0000-0000-0000-000000000011';
            UPDATE "Page" SET "IsTrashed"=false,"TrashedAt"=NULL,"TrashedById"=NULL,"TrashBatchId"=NULL WHERE "Id"='00000000-0000-0000-0000-000000000012';
            UPDATE "Board" SET "Sequence"=6,"IsTrashed"=true,"TrashedAt"='2026-05-01T00:00:00Z',"TrashedById"='00000000-0000-0000-0000-000000000001',"TrashBatchId"=gen_random_uuid()
              WHERE "Id"='00000000-0000-0000-0000-0000000000b1';
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              VALUES('00000000-0000-0000-0000-000000000013','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000002',1);
            """);
        Assert.Equal($"11:space@a v4 by 1 owner - updated {today} trashed -, 12:space@a v1 by 2 owner - updated {today} trashed -, 13:space@a v1 by 2 owner - updated {today} trashed -, b1:space@a v6 by 2 owner - updated {today} trashed 05-01",
            await db.ScalarAsync(envelope));

        // Scope rules of the objects the platform stores: a space needs its space, the wiki none, a private object its owner.
        await db.ExecuteAsync("""
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","Data") VALUES
              (gen_random_uuid(),'test.fields','wiki',NULL,NULL,1,now(),'00000000-0000-0000-0000-000000000001',now(),'{}'),
              (gen_random_uuid(),'test.fields','private',NULL,'00000000-0000-0000-0000-000000000001',1,now(),'00000000-0000-0000-0000-000000000001',now(),'{}');
            """);
        foreach (var (scope, space, owner) in new[] { ("space", "NULL", "NULL"), ("wiki", "'00000000-0000-0000-0000-00000000000a'", "NULL"), ("private", "NULL", "NULL"), ("team", "NULL", "NULL") })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync($"""
                INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","OwnerId","Version","CreatedAt","UpdatedAt")
                  VALUES(gen_random_uuid(),'test.fields','{scope}',{space},{owner},1,now(),now())
                """));
            Assert.Equal("CK_ContentResource_Scope", refused.ConstraintName);
        }
    }

    [PostgreSqlFact]
    public async Task Personal_items_migration_keeps_space_boards_and_page_grants_and_admits_item_grants()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await ApplyBeforeAsync(db, 2026102930);
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES
              ('00000000-0000-0000-0000-000000000001','owner','Owner',false,true,'stamp'),
              ('00000000-0000-0000-0000-000000000002','reader','Reader',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES
              ('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "ResourceGrant"("Id","PageId","UserId","Role") VALUES
              ('00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-000000000002',1);
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a','Plan',2,now(),'00000000-0000-0000-0000-000000000001',now(),false,1024);
            """);

        // Up to 2026110930, which moved what these triggers did to C# (ContentRowEffects).
        await ApplyBeforeAsync(db, 2026110930, from: 2026102930);
        const string envelopes = """
            SELECT string_agg(right("Id"::text,2)||':'||"TypeId"||' '||"Scope"||' owner '||COALESCE(right("OwnerId"::text,1),'-')||' '||COALESCE("Title",'-'),', ' ORDER BY "Id")
            FROM "ContentResource"
            """;
        // An existing board stays a space object and now carries its title; the page and its grant are as they were.
        Assert.Equal("11:atlas.page space owner - -, b1:atlas.board space owner - Plan", await db.ScalarAsync(envelopes));
        Assert.Equal("1:11", await db.ScalarAsync("""SELECT "Role"||':'||right("PageId"::text,2) FROM "ResourceGrant" """));

        // A renaming batch moves a board's envelope title along; a private note is shared by a grant on its object.
        await db.ExecuteAsync("""
            UPDATE "Board" SET "Title"='Plan 2',"Sequence"=3 WHERE "Id"='00000000-0000-0000-0000-0000000000b1';
            INSERT INTO "ContentResource"("Id","TypeId","Scope","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","Title","Data") VALUES
              ('00000000-0000-0000-0000-0000000000b2','atlas.note','private','00000000-0000-0000-0000-000000000001',1,now(),'00000000-0000-0000-0000-000000000001',now(),'Mine','{"title":"Mine","text":""}');
            INSERT INTO "ResourceGrant"("Id","ObjectId","UserId","Role") VALUES
              ('00000000-0000-0000-0000-0000000000c2','00000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-000000000002',2);
            """);
        Assert.Equal("11:atlas.page space owner - -, b1:atlas.board space owner - Plan 2, b2:atlas.note private owner 1 Mine", await db.ScalarAsync(envelopes));
        Assert.Equal(2, await db.ScalarAsync(GrantRole("00000000-0000-0000-0000-0000000000b2", "00000000-0000-0000-0000-000000000002")));
        Assert.Equal(DBNull.Value, await db.ScalarAsync(GrantRole("00000000-0000-0000-0000-0000000000b2", "00000000-0000-0000-0000-000000000001")));

        // An item grant has one object and a read or write role.
        foreach (var (sql, constraint) in new[]
                 {
                     ("""INSERT INTO "ResourceGrant"("Id","ObjectId","UserId","Role") VALUES (gen_random_uuid(),'00000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-000000000001',3)""", "CK_ResourceGrant_Role"),
                     ("""INSERT INTO "ResourceGrant"("Id","ObjectId","PageId","UserId","Role") VALUES (gen_random_uuid(),'00000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-000000000001',1)""", "CK_ResourceGrant_Target")
                 })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(sql));
            Assert.Equal(constraint, refused.ConstraintName);
        }
    }

    /// <summary>„Co je nového“ (3.5 K): every existing account has seen 3.4.3, so each gets the notice of the next release once; an account
    /// created later has no row and reads as new.</summary>
    [PostgreSqlFact]
    public async Task Release_notes_migration_marks_existing_accounts_as_having_seen_3_4_3()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await ApplyBeforeAsync(db, 2026110540);
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES
              ('00000000-0000-0000-0000-000000000001','active','Active',false,true,'stamp'),
              ('00000000-0000-0000-0000-000000000002','inactive','Inactive',false,false,'stamp');
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);

        Assert.Equal("01=3.4.3, 02=3.4.3", await db.ScalarAsync("""SELECT string_agg(right("UserId"::text,2)||'='||"SeenVersion", ', ' ORDER BY "UserId") FROM "UserReleaseNotes" """));
        var refused = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("""UPDATE "UserReleaseNotes" SET "SeenVersion"='3.5.0-dev'"""));
        Assert.Equal("CK_UserReleaseNotes_SeenVersion", refused.ConstraintName);
    }

    [PostgreSqlFact]
    public async Task Personal_owner_index_covers_existing_private_objects_and_leaves_space_objects_out()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await ApplyBeforeAsync(db, 2026102990);
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES
              ('00000000-0000-0000-0000-000000000001','owner','Owner',false,true,'stamp');
            INSERT INTO "ContentResource"("Id","TypeId","Scope","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","Title","Data") VALUES
              ('00000000-0000-0000-0000-0000000000c1','atlas.note','private','00000000-0000-0000-0000-000000000001',1,now(),'00000000-0000-0000-0000-000000000001',now(),'Mine','{"title":"Mine","text":"x"}'),
              ('00000000-0000-0000-0000-0000000000c2','atlas.contact','wiki',NULL,1,now(),'00000000-0000-0000-0000-000000000001',now(),'Jana','{"name":"Jana"}');
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);

        Assert.EndsWith("""USING btree ("OwnerId", "UpdatedAt" DESC) WHERE (("Scope")::text = 'private'::text)""",
            (string?)await db.ScalarAsync("""SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_ContentResource_Owner'"""));
        Assert.Equal("c1=Mine, c2=Jana", await db.ScalarAsync("""SELECT string_agg(right("Id"::text,2)||'='||"Title", ', ' ORDER BY "Id") FROM "ContentResource" """));
    }

    /// <summary>Personal boards dropped (owner, 29. 10. 2026): a development database's personal board becomes its owner's private note with
    /// the same id, title and its live card texts in reading order, its shares kept; a trashed one a trashed note. Space boards stay.</summary>
    [PostgreSqlFact]
    public async Task Dropping_personal_boards_turns_each_into_its_owners_note_and_keeps_its_shares_and_space_boards()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await ApplyBeforeAsync(db, 2026102991);
        await db.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES
              ('00000000-0000-0000-0000-000000000001','owner','Owner',false,true,'stamp'),
              ('00000000-0000-0000-0000-000000000002','reader','Reader',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Board"("Id","SpaceId","OwnerId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","TrashedAt","TrashedById","TrashBatchId","SortOrder") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a',NULL,'Plan',2,now(),'00000000-0000-0000-0000-000000000001',now(),false,NULL,NULL,NULL,1024),
              ('00000000-0000-0000-0000-0000000000b2',NULL,'00000000-0000-0000-0000-000000000001','Nápady',3,now(),'00000000-0000-0000-0000-000000000001',now(),false,NULL,NULL,NULL,1024),
              ('00000000-0000-0000-0000-0000000000b3',NULL,'00000000-0000-0000-0000-000000000001','Stará',1,now(),'00000000-0000-0000-0000-000000000001',now(),true,now(),'00000000-0000-0000-0000-000000000001',gen_random_uuid(),1024);
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Text","Version","IsDeleted") VALUES
              (gen_random_uuid(),'00000000-0000-0000-0000-0000000000b2','note',300,0,100,100,1,'**druhá**',1,false),
              (gen_random_uuid(),'00000000-0000-0000-0000-0000000000b2','note',0,0,100,100,2,'první',1,false),
              (gen_random_uuid(),'00000000-0000-0000-0000-0000000000b2','note',0,500,100,100,3,'smazaná',1,true),
              (gen_random_uuid(),'00000000-0000-0000-0000-0000000000b1','note',0,0,100,100,1,'v prostoru',1,false);
            INSERT INTO "ResourceGrant"("Id","ObjectId","UserId","Role") VALUES
              ('00000000-0000-0000-0000-0000000000c2','00000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-000000000002',1);
            """);

        await AtlasMigrations.MigrateAsync(db.ConnectionString);

        Assert.Equal("b1:atlas.board space Plan live, b2:atlas.note private Nápady live, b3:atlas.note private Stará trashed", await db.ScalarAsync("""
            SELECT string_agg(right("Id"::text,2)||':'||"TypeId"||' '||"Scope"||' '||"Title"||' '||CASE WHEN "TrashedAt" IS NULL THEN 'live' ELSE 'trashed' END, ', ' ORDER BY "Id")
            FROM "ContentResource"
            """));
        Assert.Equal("""{"text": "první\n\n**druhá**", "title": "Nápady"}""",
            await db.ScalarAsync("""SELECT "Data"::text FROM "ContentResource" WHERE "Id"='00000000-0000-0000-0000-0000000000b2'"""));
        Assert.Equal(2L, await db.ScalarAsync("""SELECT count(*) FROM "ObjectChange" WHERE "Version" = 1"""));
        Assert.Equal(1, await db.ScalarAsync(GrantRole("00000000-0000-0000-0000-0000000000b2", "00000000-0000-0000-0000-000000000002")));
        Assert.Equal("b1:1", await db.ScalarAsync("""SELECT string_agg(right(b."Id"::text,2)||':'||(SELECT count(*) FROM "BoardItem" i WHERE i."BoardId"=b."Id"), ', ') FROM "Board" b"""));
        Assert.Equal(0L, await db.ScalarAsync("""SELECT count(*) FROM information_schema.columns WHERE table_name='Board' AND column_name='OwnerId' AND table_schema=current_schema()"""));
        var refused = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("""
            INSERT INTO "Board"("Id","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder") VALUES (gen_random_uuid(),'None',0,now(),'00000000-0000-0000-0000-000000000001',now(),false,1024)
            """));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, refused.SqlState);
    }

    /// <summary>The object grant role (AccessSql, the rule atlas_object_grant_role was) of an account on an object.</summary>
    private static string GrantRole(string objectId, string account) => $"""
        SELECT {AccessSql.ObjectGrantRole("x.\"o\"", "x.\"a\"")} FROM (VALUES (CAST('{objectId}' AS uuid), CAST('{account}' AS uuid))) x("o","a")
        """;

    /// <summary>The schema as an older release left it: every script before <paramref name="version"/> with its journal row. One
    /// connection for all of them; a connection per statement exhausted the client's ports when the whole suite ran at once.</summary>
    /// <param name="from">The first script to apply, to go on from an earlier call; 0 starts an empty database.</param>
    private static async Task ApplyBeforeAsync(MigrationDatabase db, int version, int from = 0)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        var scripts = from == 0 ? new List<string> { "CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())" } : [];
        foreach (var resource in typeof(AtlasDatabase).Assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version >= from && item.Version < version).OrderBy(item => item.Version))
        {
            scripts.Add(await EmbeddedSqlAsync(resource.Name.Split("Migrations.")[^1]));
            scripts.Add($"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
        foreach (var script in scripts)
        {
            await using var command = new NpgsqlCommand(script, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string> EmbeddedSqlAsync(string file)
    {
        using var stream = typeof(AtlasDatabase).Assembly.GetManifestResourceStream("Atlas.Server.Persistence.Migrations." + file)
            ?? throw new FileNotFoundException(file);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Trait("Category", "Smoke")]
    [Trait("Category", "Guard")]
    [Fact]
    public void Offline_script_contains_registered_migrations_and_matches_every_embedded_version()
    {
        var assembly = typeof(AtlasMigrations).Assembly;
        var embedded = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name => int.Parse(name.Split('.')[^2].Split('_')[0])).Order().ToArray();
        Assert.Equal(embedded, DeclaredVersions());
        var script = AtlasMigrationScript.Generate();
        Assert.Contains("CREATE TABLE \"Workspace\"", script);
        Assert.Contains("CREATE TABLE \"ServerRecoveryToken\"", script);
        Assert.Contains("timestamptz", script);
        Assert.Contains("atlas_search_published", script);
        Assert.StartsWith("-- Atlas", script);
        Assert.EndsWith("COMMIT;" + Environment.NewLine, script);
    }

    [PostgreSqlFact]
    public async Task Exported_fresh_schema_script_executes_and_is_adopted_on_startup()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync(AtlasMigrationScript.Generate());
        var count = await db.ScalarAsync("SELECT count(*) FROM \"SchemaVersion\"");
        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        Assert.Equal(count, await db.ScalarAsync("SELECT count(*) FROM \"SchemaVersion\""));
        Assert.True(Convert.ToInt64(count) >= 9);
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Fresh_schema_and_concurrent_restarts_apply_each_version_once()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        var started = (DateTime)(await db.ScalarAsync("SELECT clock_timestamp()"))!;
        await Task.WhenAll(AtlasMigrations.MigrateAsync(db.ConnectionString), AtlasMigrations.MigrateAsync(db.ConnectionString));
        var finished = (DateTime)(await db.ScalarAsync("SELECT clock_timestamp()"))!;
        // Every declared version exactly once, and all of them committed by one transaction: one runner applied the whole
        // batch while the other waited for the advisory lock and found nothing left to do.
        Assert.Equal(string.Join(",", DeclaredVersions()), await db.ScalarAsync("SELECT string_agg(\"Version\"::text, ',' ORDER BY \"Version\") FROM \"SchemaVersion\""));
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(DISTINCT xmin::text) FROM \"SchemaVersion\""));
        // AppliedAt is that transaction's start, written by FluentMigrator as UTC wall time; under this connection's
        // Europe/Prague session it is the right instant only because the runner sets UTC for the batch. Bracketing it by
        // the server clock around the run catches an hour-scale shift however long the migration takes on a busy host.
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(DISTINCT \"AppliedAt\") FROM \"SchemaVersion\""));
        Assert.InRange((DateTime)(await db.ScalarAsync("SELECT min(\"AppliedAt\") FROM \"SchemaVersion\""))!, started, finished);
        Assert.Equal("timestamp with time zone", await db.ScalarAsync("SELECT data_type FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='SchemaVersion' AND column_name='AppliedAt'"));
        Assert.NotEqual(DBNull.Value, await db.ScalarAsync("SELECT to_regclass('\"PagePublication\"')::text"));
    }

    [PostgreSqlFact]
    public async Task Legacy_journal_is_adopted_without_rerunning_or_rewriting_applied_version()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        using var stream = typeof(AtlasDatabase).Assembly.GetManifestResourceStream("Atlas.Server.Persistence.Migrations.001_initial.sql")!;
        using var reader = new StreamReader(stream);
        await db.ExecuteAsync(await reader.ReadToEndAsync());
        await db.ExecuteAsync("INSERT INTO \"SchemaVersion\" VALUES(1,'2026-01-01T00:00:00Z'); INSERT INTO \"Workspace\" VALUES(1,'Preserved wiki',now())");
        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        Assert.Equal("Preserved wiki", await db.ScalarAsync("SELECT \"Name\" FROM \"Workspace\""));
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), await db.ScalarAsync("SELECT \"AppliedAt\" FROM \"SchemaVersion\" WHERE \"Version\"=1"));
        Assert.True(Convert.ToBoolean(await db.ScalarAsync("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='SchemaVersion' AND column_name='Description')::text")));
    }

    [PostgreSqlFact]
    public async Task Failed_batch_rolls_back_schema_and_journal_and_can_be_retried()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"Space\" (\"Sentinel\" integer)");
        await Assert.ThrowsAnyAsync<Exception>(() => AtlasMigrations.MigrateAsync(db.ConnectionString));
        Assert.Equal(DBNull.Value, await db.ScalarAsync("SELECT to_regclass(quote_ident(current_schema()) || '.\"UserAccount\"')::text"));
        Assert.Equal(DBNull.Value, await db.ScalarAsync("SELECT to_regclass(quote_ident(current_schema()) || '.\"SchemaVersion\"')::text"));
        await db.ExecuteAsync("DROP TABLE \"Space\"");
        await AtlasMigrations.MigrateAsync(db.ConnectionString);
        Assert.True(Convert.ToInt64(await db.ScalarAsync("SELECT count(*) FROM \"SchemaVersion\"")) >= 7);
    }

    [PostgreSqlFact]
    public async Task Unknown_newer_journal_version_is_rejected_without_changes()
    {
        await using var db = await MigrationDatabase.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now()); INSERT INTO \"SchemaVersion\"(\"Version\") VALUES(99999)");
        await Assert.ThrowsAsync<ApiException>(() => AtlasMigrations.MigrateAsync(db.ConnectionString));
        Assert.Equal(99999, await db.ScalarAsync("SELECT \"Version\" FROM \"SchemaVersion\""));
        Assert.Equal(DBNull.Value, await db.ScalarAsync("SELECT to_regclass(quote_ident(current_schema()) || '.\"UserAccount\"')::text"));
    }

    private static int[] DeclaredVersions() => typeof(AtlasMigrations).Assembly.GetTypes()
        .Select(type => type.GetCustomAttributes(typeof(FluentMigrator.MigrationAttribute), false)
            .Cast<FluentMigrator.MigrationAttribute>().SingleOrDefault()?.Version)
        .Where(version => version.HasValue).Select(version => (int)version!.Value).Order().ToArray();

    /// <summary>An empty schema in its own database: these tests migrate it themselves, and a database of their own keeps
    /// the migration advisory lock away from every other test.</summary>
    private sealed class MigrationDatabase(string connectionString) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public static async Task<MigrationDatabase> CreateAsync()
        {
            var schema = "atlas_migration_" + Guid.NewGuid().ToString("N");
            var settings = new NpgsqlConnectionStringBuilder(await Platform.TestDatabases.CreateEmptyAsync(schema))
            {
                SearchPath = schema + ",public",
                Timezone = "Europe/Prague"
            };
            return new(settings.ConnectionString);
        }

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteScalarAsync();
        }

        public async Task ExecuteAsync(string sql) => _ = await ScalarAsync(sql);

        /// <summary>The same database with pg_catalog searched after the test schema, so schema-local views shadow catalog views.</summary>
        public string WithSearchPathBeforeCatalog()
        {
            var settings = new NpgsqlConnectionStringBuilder(ConnectionString);
            settings.SearchPath = settings.SearchPath!.Split(',')[0] + ",pg_catalog,public";
            return settings.ConnectionString;
        }

        public ValueTask DisposeAsync() => new(Platform.TestDatabases.DropAsync(ConnectionString));
    }
}
