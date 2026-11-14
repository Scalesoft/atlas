using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Boards;

/// <summary>Migration 2026102820 over the boards an installation already has: they stay boards of the tree (no purpose); a page's own
/// board needs its page, a purpose of the type-id shape, and a page has one live board of each purpose (a trashed one aside).</summary>
public sealed class PageBoardsMigrationTests
{
    private const long Version = 2026102820;

    [PostgreSqlFact]
    public async Task Existing_boards_keep_no_purpose_and_a_page_has_one_live_board_of_each_purpose()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_page_boards_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, Version);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              VALUES('00000000-0000-0000-0000-0000000000f1','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-000000000001',1);
            INSERT INTO "Board"("Id","SpaceId","ParentPageId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder") VALUES
              ('00000000-0000-0000-0000-0000000000b1','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-0000000000f1','One',2,now(),
               '00000000-0000-0000-0000-000000000001',now(),false,1024);
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "Board" WHERE "Purpose" IS NOT NULL"""));
        await ExecuteAsync(schema.ConnectionString, """UPDATE "Board" SET "Purpose"='projector.deployment'""");
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString, """UPDATE "Board" SET "ParentPageId"=NULL"""));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString, """UPDATE "Board" SET "Purpose"='Deployment'"""));
        const string second = """
            INSERT INTO "Board"("Id","SpaceId","ParentPageId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder","Purpose") VALUES
              ('00000000-0000-0000-0000-0000000000b2','00000000-0000-0000-0000-00000000000a','00000000-0000-0000-0000-0000000000f1','Two',0,now(),
               '00000000-0000-0000-0000-000000000001',now(),false,1,'projector.deployment')
            """;
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString, second));
        await ExecuteAsync(schema.ConnectionString, """
            UPDATE "Board" SET "IsTrashed"=true, "TrashedAt"=now(), "TrashedById"='00000000-0000-0000-0000-000000000001', "TrashBatchId"=gen_random_uuid()
            """);
        await ExecuteAsync(schema.ConnectionString, second);
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
