using Npgsql;

namespace Atlas.Server.Features.Backups;

/// <summary>
/// One Git synchronisation at a time: the job runs one at a time by itself, but „Synchronizovat teď“ runs in its request and must not
/// meet it in the local clone. A session-level advisory lock on its own connection, because a run spans several journal
/// transactions (docs/architecture.md, raw SQL category 2). It is scoped to the schema, so independent wiki databases and tests
/// never block each other, and released when the connection is lost.
/// </summary>
internal sealed class BackupRunLock : IAsyncDisposable
{
    internal const int GitBackup = 731145100;

    private readonly NpgsqlConnection m_connection;
    private readonly int m_key;

    private BackupRunLock(NpgsqlConnection connection, int key)
    {
        m_connection = connection;
        m_key = key;
    }

    /// <summary>The lock, or null when another run still holds it after <paramref name="wait"/>.</summary>
    public static async Task<BackupRunLock?> TryAcquireAsync(IConfiguration configuration, int key, TimeSpan wait, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(configuration.GetConnectionString("atlas-db"));
        try
        {
            await connection.OpenAsync(ct);
            var until = DateTime.UtcNow + wait;
            while (true)
            {
                await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext(current_schema()), @key)", connection);
                acquire.Parameters.AddWithValue("key", key);
                if (await acquire.ExecuteScalarAsync(ct) is true)
                    return new BackupRunLock(connection, key);
                if (DateTime.UtcNow >= until)
                {
                    await connection.DisposeAsync();
                    return null;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext(current_schema()), @key)", m_connection);
            release.Parameters.AddWithValue("key", m_key);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
        finally
        {
            await m_connection.DisposeAsync();
        }
    }
}
