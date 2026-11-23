using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Atlas.Server.Platform.Persistence;
using Xunit;

namespace Atlas.Server.Tests.Platform;

/// <summary>
/// The start rebuilds the indexes once when the database's collation version changed (CollationVersion): a volume of the former musl
/// image records none, a glibc upgrade records another. The test writes such a version into its own database (the test role is a superuser).
/// </summary>
public sealed class CollationVersionTests
{
    [PostgreSqlFact]
    public async Task A_missing_or_other_collation_version_rebuilds_every_index_once_and_records_the_version()
    {
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_collation_");
        foreach (var recorded in new[] { "NULL", "'0.0-other'" })
        {
            await ExecuteAsync(schema.ConnectionString, $"UPDATE pg_database SET datcollversion = {recorded} WHERE datname = current_database()");
            var before = await FileNodesAsync(schema.ConnectionString);

            // Two instances start at once: one rebuilds, the other waits for it and finds the version recorded.
            var rebuilt = await Task.WhenAll(
                CollationVersion.RefreshAsync(schema.ConnectionString, NullLogger.Instance, default),
                CollationVersion.RefreshAsync(schema.ConnectionString, NullLogger.Instance, default));
            Assert.Equal(0, rebuilt.Min());
            Assert.True(rebuilt.Max() >= before.Count);
            var after = await FileNodesAsync(schema.ConnectionString);
            Assert.All(before, index => Assert.NotEqual(index.Value, after[index.Key]));
            Assert.Equal(true, await ScalarAsync(schema.ConnectionString,
                "SELECT datcollversion = pg_database_collation_actual_version(oid) FROM pg_database WHERE datname = current_database()"));

            Assert.Equal(0, await CollationVersion.RefreshAsync(schema.ConnectionString, NullLogger.Instance, default));
            Assert.Equal(after, await FileNodesAsync(schema.ConnectionString));
        }
    }

    /// <summary>The storage of each index of the schema: a rebuild gives it a new file.</summary>
    private static async Task<Dictionary<string, long>> FileNodesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT c.relname, c.relfilenode::bigint FROM pg_class c WHERE c.relkind = 'i' AND c.relnamespace = current_schema()::regnamespace
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var nodes = new Dictionary<string, long>();
        while (await reader.ReadAsync())
        {
            nodes[reader.GetString(0)] = reader.GetInt64(1);
        }
        Assert.NotEmpty(nodes);
        return nodes;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
