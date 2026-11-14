using Npgsql;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class PostgreSqlTestConnectionTests
{
    [PostgreSqlFact]
    public async Task Disposed_isolated_fixture_connections_do_not_accumulate_backend_sessions()
    {
        var tag = "atlas-pool-regression-" + Guid.NewGuid().ToString("N");
        var strings = new List<string>();
        try
        {
            // Unique SearchPath is part of Npgsql's pool key, exactly as in real per-test schemas.
            // Twelve disposed fixtures reproduce the leak without exhausting the shared test server.
            for (var i = 0; i < 12; i++)
            {
                var settings = new NpgsqlConnectionStringBuilder(PostgreSqlTestConnection.Read("ATLAS_TEST_CONNECTION"))
                {
                    ApplicationName = tag,
                    SearchPath = "atlas_unused_" + i + ",public"
                };
                strings.Add(settings.ConnectionString);
                await using var connection = new NpgsqlConnection(settings.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                Assert.Equal(1, await command.ExecuteScalarAsync());
            }
            var observerSettings = new NpgsqlConnectionStringBuilder(PostgreSqlTestConnection.Read("ATLAS_TEST_CONNECTION")) { Pooling = false };
            await using var observer = new NpgsqlConnection(observerSettings.ConnectionString);
            await observer.OpenAsync();
            await using var count = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name=@tag", observer);
            count.Parameters.AddWithValue("tag", tag);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
        finally
        {
            // Clear only this regression's exact pools if the RED version retained them.
            foreach (var connectionString in strings)
            {
                using var connection = new NpgsqlConnection(connectionString);
                NpgsqlConnection.ClearPool(connection);
            }
        }
    }
}
