using Npgsql;

namespace Atlas.Server.Platform.Persistence;

/// <summary>
/// The database role the running application works as, when the schema's owner has a connection of its own
/// (<c>ConnectionStrings:atlas-db-owner</c>): migrations, statistics and the installation restore run as the owner, every request,
/// job and listener as the role of <c>ConnectionStrings:atlas-db</c>. At each start, after the migrations, the owner keeps that role a
/// plain login (no superuser, CREATEDB, CREATEROLE, replication or row-security bypass) with the connection string's password and
/// these rights on the schema: the rows of every table and sequence, but on the audit facts no UPDATE, DELETE or TRUNCATE and on the
/// access history only closing an interval (UPDATE of "ActiveTo"). Permissions, not logic: IntegrityRules holds the same rules inside
/// the application (IntegrityRulesConventionTests reads these lists); these hold for anything that reaches the database as the runtime
/// role. Without an owner connection (development, tests, a single-role deployment) nothing here runs.
/// </summary>
public static class RuntimeDatabaseRole
{
    public const string OwnerConnectionName = "atlas-db-owner";

    /// <summary>Audit facts: inserted, never changed or removed.</summary>
    public static readonly IReadOnlyList<string> AppendOnly =
        ["AccessAuditEvent", "ApiActivity", "BackupDestinationChange", "HubChange", "PageAccessEvent", "RevokedBrowserSession", "SecretReleaseEvent", "AiProfileRevision"];

    /// <summary>Access history: an interval closes once (its "ActiveTo") and is never removed.</summary>
    public static readonly IReadOnlyList<string> Intervals =
    [
        "UserAccountRevision", "UserGroupRevision", "GroupMember", "SpaceGrant", "UserApiPermission", "ResourceGrant", "PageAccessModeRevision",
        "ProtectionSetting", "SecurityConfigurationRevision", "TrustedAddressRevision", "VaultLocationRevision", "HubConnectionRevision",
        "HubGroupBindingRevision", "McpSetting", "GroupAiPolicy", "UserAiPolicy",
    ];

    /// <summary>
    /// At startup, before anything connects as the runtime role (the OIDC settings loader, Wolverine, the listeners): a fresh install,
    /// the first start after an upgrade or a changed password would otherwise refuse their login. Fails soft; the database initializer
    /// runs <see cref="EnsureAsync"/> again with its retries.
    /// </summary>
    public static async Task PrepareAsync(IConfiguration configuration, ILogger logger, CancellationToken ct = default)
    {
        if (configuration.GetConnectionString(OwnerConnectionName) is not { Length: > 0 } owner
            || configuration.GetConnectionString("atlas-db") is not { Length: > 0 } runtime || owner == runtime)
        {
            return;
        }
        try
        {
            await EnsureAsync(owner, runtime, ct);
        }
        catch (NpgsqlException ex)
        {
            logger.LogWarning("The runtime database role is not prepared yet ({ErrorType}); the database initializer retries.", ex.GetType().Name);
        }
    }

    /// <summary>Creates or updates the runtime role and its rights, as the owner; nothing when both strings name the same role.</summary>
    public static async Task EnsureAsync(string ownerConnectionString, string runtimeConnectionString, CancellationToken ct)
    {
        var runtime = new NpgsqlConnectionStringBuilder(runtimeConnectionString);
        var role = runtime.Username is { Length: > 0 } name ? name : throw new InvalidOperationException("ConnectionStrings:atlas-db names no Username.");
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(ct);
        if (await ScalarAsync<string>(connection, null, "SELECT current_user", ct) == role)
        {
            return;
        }
        // The password is sent only when the role cannot log in with it (missing, or changed in the connection string), not at every start.
        var password = await CanLogInAsync(runtime, ct) ? null : runtime.Password;
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Every statement is written by format(): %I quotes the role, schema and table names, %L the password.
        var statements = new List<string>
        {
            await ScalarAsync<string>(connection, transaction, """
                SELECT format('%s ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS%s',
                    CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role) THEN 'ALTER' ELSE 'CREATE' END, @role,
                    CASE WHEN @password IS NULL THEN '' ELSE format(' PASSWORD %L', @password) END)
                """, ct, ("role", role), ("password", password)),
            await ScalarAsync<string>(connection, transaction, "SELECT format('GRANT CONNECT, TEMPORARY ON DATABASE %I TO %I', current_database(), @role)", ct, ("role", role)),
            await ScalarAsync<string>(connection, transaction, "SELECT format('GRANT USAGE ON SCHEMA %I TO %I', current_schema(), @role)", ct, ("role", role)),
            // MAINTAIN (PostgreSQL 17+): the statistics refresh after a setup or an import (DatabaseStatistics) analyzes its tables.
            await ScalarAsync<string>(connection, transaction, """
                SELECT format('GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE%s ON ALL TABLES IN SCHEMA %I TO %I',
                    CASE WHEN current_setting('server_version_num')::int >= 170000 THEN ', MAINTAIN' ELSE '' END, current_schema(), @role)
                """, ct, ("role", role)),
            await ScalarAsync<string>(connection, transaction, "SELECT format('GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA %I TO %I', current_schema(), @role)", ct, ("role", role)),
        };
        await using (var guarded = new NpgsqlCommand("""
            SELECT format('REVOKE UPDATE, DELETE, TRUNCATE ON %I.%I FROM %I', current_schema(), t.name, @role)
                || CASE WHEN t.name = ANY(@intervals) THEN format('; GRANT UPDATE (%I) ON %I.%I TO %I', 'ActiveTo', current_schema(), t.name, @role) ELSE '' END
            FROM unnest(@tables) AS t(name)
            WHERE EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = current_schema() AND tablename = t.name)
            """, connection, transaction))
        {
            guarded.Parameters.AddWithValue("role", role);
            guarded.Parameters.AddWithValue("tables", AppendOnly.Concat(Intervals).ToArray());
            guarded.Parameters.AddWithValue("intervals", Intervals.ToArray());
            await using var reader = await guarded.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                statements.Add(reader.GetString(0));
            }
        }
        foreach (var statement in statements)
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static async Task<bool> CanLogInAsync(NpgsqlConnectionStringBuilder runtime, CancellationToken ct)
    {
        try
        {
            await using var login = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(runtime.ConnectionString) { Pooling = false }.ConnectionString);
            await login.OpenAsync(ct);
            return true;
        }
        // PostgreSQL answers a missing role like a wrong password.
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification)
        {
            return false;
        }
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken ct,
        params (string Name, string? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });
        }
        return (T)(await command.ExecuteScalarAsync(ct))!;
    }
}
