using Npgsql;

namespace Atlas.Server.Platform.Persistence;

/// <summary>
/// Indexes on text are ordered by the database's collation, which a C library or ICU implements. When that changes under existing data
/// (a volume of the former Alpine/musl image opened by the Debian/glibc one, a glibc upgrade under a libc database), the indexes no
/// longer match it. PostgreSQL records the version in pg_database.datcollversion (NULL on musl, which has none) and reports the current
/// one; when they differ, the start rebuilds every index once, before the migrations use them, and records the current version, so the
/// next start finds nothing to do. Databases of docker-compose.yml use the builtin provider, whose version never changes.
/// A rebuild before serving, not a background job: 509 indexes of a production copy took 0.65 s, and until it ends a unique index
/// could admit a duplicate. CONCURRENTLY would only slow it down here and cannot run in the one transaction that records the version.
/// </summary>
public static class CollationVersion
{
    private const long LockKey = 731145013;

    /// <summary>Rebuilds the indexes when the recorded collation version differs from the server's; returns how many it rebuilt.</summary>
    public static async Task<int> RefreshAsync(string connectionString, ILogger logger, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Instances that start together: one rebuilds, the others wait here and then find the version recorded.
        await ExecuteAsync(connection, $"SELECT pg_advisory_xact_lock({LockKey})", ct);
        string database;
        string? recorded, actual;
        bool owner, superuser;
        await using (var read = new NpgsqlCommand("""
            SELECT datname, datcollversion, pg_database_collation_actual_version(oid), pg_has_role(datdba, 'USAGE'),
                   (SELECT rolsuper FROM pg_roles WHERE rolname = current_user)
            FROM pg_database WHERE datname = current_database()
            """, connection))
        await using (var row = await read.ExecuteReaderAsync(ct))
        {
            await row.ReadAsync(ct);
            database = row.GetString(0);
            recorded = row.IsDBNull(1) ? null : row.GetString(1);
            actual = row.IsDBNull(2) ? null : row.GetString(2);
            owner = row.GetBoolean(3);
            superuser = row.GetBoolean(4);
        }
        if (recorded == actual)
        {
            return 0;
        }
        // REFRESH COLLATION VERSION only replaces one version by another (PostgreSQL refuses NULL to a version); the version after
        // musl is written by a superuser, which the owner of docker-compose.yml is.
        var record = recorded is not null && actual is not null
            ? owner ? $"ALTER DATABASE \"{database.Replace("\"", "\"\"")}\" REFRESH COLLATION VERSION" : null
            : superuser ? "UPDATE pg_database SET datcollversion = pg_database_collation_actual_version(oid) WHERE datname = current_database()" : null;
        if (record is null)
        {
            logger.LogError("The collation of database {Database} changed ({Recorded} -> {Actual}), but this role may not record the new version: "
                + "text indexes may return wrong results. As its owner or a superuser run REINDEX DATABASE and record the version "
                + "(docs/operations.md, \"Kolace databáze\").", database, recorded ?? "none", actual ?? "none");
            return 0;
        }
        var indexes = new List<string>();
        await using (var list = new NpgsqlCommand("""
            SELECT format('%I.%I', n.nspname, c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'i' AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_toast%'
              AND n.nspname NOT LIKE 'pg\_temp%'
            ORDER BY 1
            """, connection))
        await using (var rows = await list.ExecuteReaderAsync(ct))
        {
            while (await rows.ReadAsync(ct))
            {
                indexes.Add(rows.GetString(0));
            }
        }
        logger.LogWarning("The collation of database {Database} changed ({Recorded} -> {Actual}); rebuilding {Count} indexes before the migrations.",
            database, recorded ?? "none", actual ?? "none", indexes.Count);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var index in indexes)
        {
            await ExecuteAsync(connection, "REINDEX INDEX " + index, ct);
        }
        await ExecuteAsync(connection, record, ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Rebuilt {Count} indexes of database {Database} in {Elapsed} ms; it records collation version {Actual}.",
            indexes.Count, database, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, actual ?? "none");
        return indexes.Count;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        // No timeout: a large vector index takes as long as it takes, and the start cannot go on without it.
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct);
    }
}
