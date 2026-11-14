using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class SqlRecordingTests
{
    /// <summary>
    /// The pinned statement counts and lock traces are read from recordings: one that stopped recording when another was opened
    /// inside it would let them pass with statements missing.
    /// </summary>
    [PostgreSqlFact]
    public async Task A_recording_opened_inside_another_does_not_end_it()
    {
        await using var connection = new NpgsqlConnection(PostgreSqlTestConnection.Read("ATLAS_TEST_CONNECTION"));
        await connection.OpenAsync();
        using var outer = SqlRecording.Start();
        await SendAsync("SELECT 1");
        using (var inner = SqlRecording.Start())
        {
            await SendAsync("SELECT 2");
            Assert.Equal(["SELECT 2"], inner.Statements);
        }
        await SendAsync("SELECT 3");

        Assert.Equal(["SELECT 1", "SELECT 2", "SELECT 3"], outer.Statements);

        async Task SendAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteScalarAsync();
        }
    }
}
