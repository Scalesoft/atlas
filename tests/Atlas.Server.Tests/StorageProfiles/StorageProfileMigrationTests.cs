using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.StorageProfiles;

public sealed class StorageProfileMigrationTests
{
    [PostgreSqlFact]
    public async Task Legacy_backup_destination_and_ciphertext_are_copied_without_changing_legacy_fields()
    {
        await using var schema = await DisposableSchema.CreateAsync("storage_upgrade_");
        await using var connection = new NpgsqlConnection(schema.ConnectionString);
        await connection.OpenAsync();
        var protector = new EphemeralDataProtectionProvider().CreateProtector("ScaleAtlas.BackupDestination.Password.v1");
        var encrypted = protector.Protect("migration-test-password");
        await using (var create = new NpgsqlCommand("CREATE TABLE \"Space\" (\"Id\" uuid PRIMARY KEY)", connection))
            await create.ExecuteNonQueryAsync();
        await Apply("2026091001_backups.sql");
        await using (var configure = new NpgsqlCommand("UPDATE \"BackupSettings\" SET \"Destination\"='smb://files/wiki/backups', \"Username\"='backup', \"Domain\"='example', \"ProtectedPassword\"=@password, \"Enabled\"=true", connection))
        {
            configure.Parameters.AddWithValue("password", encrypted);
            await configure.ExecuteNonQueryAsync();
        }
        await Apply("2026100890_storage_profiles.sql");
        await using var read = new NpgsqlCommand("SELECT p.\"Destination\", p.\"ProtectedPassword\", b.\"ProtectedPassword\", b.\"Folder\", p.\"AllowPageExports\", b.\"Enabled\" FROM \"BackupSettings\" b JOIN \"StorageProfile\" p ON p.\"Id\"=b.\"StorageProfileId\"", connection);
        await using var row = await read.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal("smb://files/wiki/backups", row.GetString(0));
        Assert.Equal(encrypted, row.GetString(1));
        Assert.Equal(encrypted, row.GetString(2));
        Assert.Equal("migration-test-password", protector.Unprotect(row.GetString(1)));
        Assert.Equal("", row.GetString(3));
        Assert.False(row.GetBoolean(4));
        Assert.True(row.GetBoolean(5));

        async Task Apply(string name)
        {
            await using var resource = typeof(AtlasDatabase).Assembly.GetManifestResourceStream("Atlas.Server.Persistence.Migrations." + name)!;
            using var reader = new StreamReader(resource);
            await using var command = new NpgsqlCommand(await reader.ReadToEndAsync(), connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
