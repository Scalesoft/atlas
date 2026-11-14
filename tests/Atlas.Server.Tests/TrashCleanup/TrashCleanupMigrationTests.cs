using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.TrashCleanup;

/// <summary>Migration 2026102997 over the spaces an installation already has: no rule row anywhere, so every space keeps the manual
/// default and nothing is cleaned; a rule takes only its bounded days and one current row per space.</summary>
public sealed class TrashCleanupMigrationTests
{
    private const long Version = 2026102997;

    [PostgreSqlFact]
    public async Task Existing_spaces_stay_manual_and_a_rule_keeps_its_bounds()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_trash_cleanup_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, Version);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES('00000000-0000-0000-0000-000000000001','admin','Admin',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','Live','book-open','blue');
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "TrashCleanupSetting" """));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "SpaceTrashCleanupSetting" """));
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "SpaceTrashCleanupSetting"("Id","SpaceId","Automatic","Days","ChangedById","ActiveFrom")
              VALUES(gen_random_uuid(),'00000000-0000-0000-0000-00000000000a',true,60,'00000000-0000-0000-0000-000000000001',now())
            """);
        // A second current row of the space, and days out of bounds.
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "SpaceTrashCleanupSetting"("Id","SpaceId","Automatic","Days","ChangedById","ActiveFrom")
              VALUES(gen_random_uuid(),'00000000-0000-0000-0000-00000000000a',false,60,'00000000-0000-0000-0000-000000000001',now() + interval '1 day')
            """));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "TrashCleanupSetting"("Id","Scope","Automatic","Days","ChangedById","ActiveFrom")
              VALUES(gen_random_uuid(),'wiki',true,1,'00000000-0000-0000-0000-000000000001',now())
            """));
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
