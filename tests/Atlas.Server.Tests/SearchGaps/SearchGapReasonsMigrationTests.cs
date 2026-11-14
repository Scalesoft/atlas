using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.SearchGaps;

/// <summary>Plan phase 3 Q5 migrations 2026100710 and 2026100720 on an upgrade: existing misses become zero-result reasons, the
/// feedback table starts empty and its note is cleared with the message (O20).</summary>
public sealed class SearchGapReasonsMigrationTests
{
    [PostgreSqlFact]
    public async Task Upgrade_keeps_existing_misses_as_zero_and_starts_feedback_empty()
    {
        var schema = "atlas_migration_" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(await Platform.TestDatabases.CreateEmptyAsync(schema)) { SearchPath = schema + ",public", Timezone = "Europe/Prague" };
        try
        {
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync();
            await Execute(connection, "CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
            var assembly = typeof(AtlasDatabase).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                         .Select(name => (Name: name, Version: int.Parse(name.Split('.')[^2].Split('_')[0])))
                         .Where(item => item.Version < 2026100710).OrderBy(item => item.Version))
            {
                using var stream = assembly.GetManifestResourceStream(resource.Name)!;
                using var reader = new StreamReader(stream);
                await Execute(connection, await reader.ReadToEndAsync());
                await Execute(connection, $"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
            }
            await Execute(connection, """
                INSERT INTO "SearchQueryLog"("SpaceId","NormalizedQuery","DisplayQuery","UserKey","OccurredOn")
                VALUES (NULL,'vpn','vpn','\x00112233445566778899aabbccddeeff',current_date)
                """);

            await AtlasMigrations.MigrateAsync(settings.ConnectionString);

            Assert.Equal("zero", await Scalar(connection, """SELECT "Reason" FROM "SearchQueryLog" """));
            Assert.Equal(0L, await Scalar(connection, """SELECT count(*) FROM "AiFeedback" """));
            await using var refused = new NpgsqlCommand("""UPDATE "SearchQueryLog" SET "Reason"='other'""", connection);
            var violation = await Assert.ThrowsAsync<PostgresException>(() => refused.ExecuteNonQueryAsync());
            Assert.Equal("CK_SearchQueryLog_Reason", violation.ConstraintName);
        }
        finally
        {
            await Platform.TestDatabases.DropAsync(settings.ConnectionString);
        }
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
