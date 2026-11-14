using Microsoft.Extensions.Logging.Abstractions;
using NHibernate;
using Npgsql;
using Atlas.Server.Platform.Persistence;
using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class DatabaseStatisticsTests
{
    [PostgreSqlFact]
    public async Task Database_startup_initializes_access_table_statistics()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_statistics_");
        await AtlasMigrations.MigrateAsync(schema.ConnectionString);
        Assert.Equal(-1, await RelationRowsAsync(schema.ConnectionString, "UserAccount"));

        using var database = new AtlasDatabase(schema.Configuration, NullLogger<AtlasDatabase>.Instance);
        await database.InitializeAsync(default);

        Assert.Equal(0, await RelationRowsAsync(schema.ConnectionString, "UserAccount"));
        Assert.Equal(0, await RelationRowsAsync(schema.ConnectionString, "Space"));
        Assert.Equal(0, await RelationRowsAsync(schema.ConnectionString, "SpaceGrant"));
        Assert.Equal(0, await RelationRowsAsync(schema.ConnectionString, "GroupMember"));
    }

    private static async Task<long> RelationRowsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT reltuples::bigint FROM pg_class WHERE oid=to_regclass(@table)", connection);
        command.Parameters.AddWithValue("table", $"\"{table}\"");
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

internal static class PostgreSqlStatistics
{
    public static Task<long> RelationRowsAsync(this ISession session, string table)
        => session.CreateSQLQuery("SELECT CAST(reltuples AS bigint) FROM pg_class WHERE oid=to_regclass(:table)")
            .SetParameter("table", $"\"{table}\"")
            .UniqueResultAsync<long>();
}
