using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Backups;

/// <summary>Migration 2026102725 over the rows an installation already has: its settings and runs keep their mode, no key appears.</summary>
public sealed class BackupKeyMigrationTests
{
    private const long KeyVersion = 2026102725;

    [PostgreSqlFact]
    public async Task Existing_settings_and_runs_keep_their_mode_get_no_key_and_accept_the_complete_mode()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_backup_key_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, KeyVersion);
        await ExecuteAsync(schema.ConnectionString, """
            UPDATE "BackupSettings" SET "Enabled" = true, "Mode" = 'latest', "Destination" = '/srv/backups' WHERE "Id" = 1;
            INSERT INTO "BackupRun"("Id","SettingsId","StartedAt","FinishedAt","Status","Trigger","Mode","Destination","FileName","Size")
              VALUES (gen_random_uuid(), 1, '2026-10-01Z', '2026-10-01Z', 'succeeded', 'scheduled', 'latest', '/srv/backups', 'wiki.zip', 10);
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("latest|t|||", await ScalarAsync(schema.ConnectionString,
            "SELECT \"Mode\" || '|' || \"Enabled\"::text::char || '|' || coalesce(\"ProtectedBackupKey\", '') || '|' || coalesce(\"BackupKeyId\", '') || '|' "
            + "|| coalesce(\"BackupKeyCreatedAt\"::text, '') FROM \"BackupSettings\""));
        Assert.Equal("latest", await ScalarAsync(schema.ConnectionString, "SELECT \"Mode\" FROM \"BackupRun\""));
        await ExecuteAsync(schema.ConnectionString, "UPDATE \"BackupSettings\" SET \"Mode\" = 'complete', \"ProtectedBackupKey\" = 'x', \"BackupKeyId\" = '0123456789abcdef', "
            + "\"BackupKeyCreatedAt\" = now()");
        await ExecuteAsync(schema.ConnectionString, "UPDATE \"BackupRun\" SET \"Mode\" = 'complete'");
        // A key comes whole or not at all; an unknown mode stays refused.
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString, "UPDATE \"BackupSettings\" SET \"BackupKeyId\" = NULL"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString, "UPDATE \"BackupSettings\" SET \"Mode\" = 'other'"));
    }

    /// <summary>A feature/atlas-3 database ran it as 2026102720 and records that number, so it runs once more, keeping its key.</summary>
    [PostgreSqlFact]
    public async Task A_database_that_ran_it_under_its_old_number_runs_it_again_and_keeps_its_key()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_backup_key_rerun_");
        await ApplyBeforeAsync(schema.ConnectionString, KeyVersion + 1);
        await ExecuteAsync(schema.ConnectionString, $"""
            UPDATE "BackupSettings" SET "Mode" = 'complete', "ProtectedBackupKey" = 'x', "BackupKeyId" = '0123456789abcdef', "BackupKeyCreatedAt" = now();
            DELETE FROM "SchemaVersion" WHERE "Version" = {KeyVersion};
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("complete|0123456789abcdef", await ScalarAsync(schema.ConnectionString, "SELECT \"Mode\" || '|' || \"BackupKeyId\" FROM \"BackupSettings\""));
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
