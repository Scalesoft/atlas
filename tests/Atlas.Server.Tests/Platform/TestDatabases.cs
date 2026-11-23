using System.Collections.Concurrent;
using System.Net.Sockets;
using Npgsql;

namespace Atlas.Server.Tests.Platform;

/// <summary>
/// One PostgreSQL database per disposable schema, on the server <c>ATLAS_TEST_CONNECTION</c> points at (the role
/// needs CREATEDB). The migrations run once per test run into a template database; a schema that should start
/// at head is a clone of it (<c>CREATE DATABASE … TEMPLATE</c>) with the template schema renamed to the caller's
/// name, so a test pays for a file copy instead of the full migration. Separate databases also keep the
/// migration advisory lock and every schema-wide lock apart, which is what lets the suite run in parallel.
/// Each database is dropped with its fixture; whatever a crashed run leaves behind is dropped at process exit.
/// </summary>
internal static class TestDatabases
{
    private const string TemplateSchema = "atlas_template";
    // The machine in the name lets a later run tell its own machine's dead runs apart from runs of other machines (see SweepDeadRunsAsync).
    private static readonly string s_machine = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Environment.MachineName)))[..6];
    private static readonly string s_run = "atlas_t" + s_machine + "_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6];
    private static readonly ConcurrentDictionary<string, byte> s_live = new();
    private static readonly Lazy<Task> s_serverCheck = new(CheckServerAsync);
    private static readonly Lazy<Task<string>> s_template = new(CreateTemplateAsync);
    private static int s_counter;

    static TestDatabases()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DropLeftovers();
    }

    // Pooled: every fixture creates and drops a database through it, and one-use connections for that would leave
    // thousands of client sockets in TIME_WAIT (Windows then refuses new connections at 128 parallel tests).
    private static string Admin { get; } = new NpgsqlConnectionStringBuilder(PostgreSqlTestConnection.Read("ATLAS_TEST_CONNECTION")) { Pooling = true }.ConnectionString;

    /// <summary>A new database holding the empty <paramref name="schema"/>; returns its admin connection string.</summary>
    public static async Task<string> CreateEmptyAsync(string schema)
    {
        var database = await CreateDatabaseAsync(null);
        var connection = For(database);
        await ExecuteAsync(connection, $"CREATE SCHEMA {schema}");
        return connection;
    }

    /// <summary>A clone of the migrated template whose schema is renamed to <paramref name="schema"/>.</summary>
    public static async Task<string> CreateMigratedAsync(string schema)
    {
        var database = await CreateDatabaseAsync(await s_template.Value);
        var connection = For(database);
        await ExecuteAsync(connection, $"ALTER SCHEMA {TemplateSchema} RENAME TO {schema}");
        return connection;
    }

    /// <summary>Drops the database a connection string from <see cref="CreateEmptyAsync"/> or <see cref="CreateMigratedAsync"/> names, and its pool.</summary>
    public static async Task DropAsync(string connectionString)
    {
        using (var poolKey = new NpgsqlConnection(connectionString))
        {
            NpgsqlConnection.ClearPool(poolKey);
        }
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        // FORCE ends backends a disposed host or a pooled connection may still hold.
        await TestCosts.MeasureAsync("database drop", () => ExecuteAsync(Admin, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"));
        s_live.TryRemove(database, out _);
    }

    private static async Task<string> CreateDatabaseAsync(string? template)
    {
        await s_serverCheck.Value;
        var database = s_run + "_" + Interlocked.Increment(ref s_counter);
        s_live[database] = 0;
        // The default WAL_LOG strategy copies the ~11 MB template in milliseconds on Linux and, unlike FILE_COPY,
        // does not force a checkpoint per clone while dozens of clones are created at once. Text sorts as in docker-compose.yml
        // (builtin C.UTF-8, by code point) whatever C library and locale the test server has; clones inherit it.
        await TestCosts.MeasureAsync(template is null ? "database empty" : "database clone",
            () => ExecuteAsync(Admin, template is null
                ? $"CREATE DATABASE {database} TEMPLATE template0 LOCALE_PROVIDER builtin BUILTIN_LOCALE 'C.UTF-8'"
                : $"CREATE DATABASE {database} TEMPLATE {template}"));
        return database;
    }

    private static async Task<string> CreateTemplateAsync()
    {
        var template = await CreateDatabaseAsync(null);
        var connection = new NpgsqlConnectionStringBuilder(For(template)) { SearchPath = TemplateSchema + ",public" }.ConnectionString;
        await ExecuteAsync(connection, $"CREATE SCHEMA {TemplateSchema}");
        await TestCosts.MeasureAsync("template migrate", () => AtlasMigrations.MigrateAsync(connection));
        return template;
    }

    /// <summary>
    /// A native Windows PostgreSQL needs 5-60 s per CREATE DATABASE and about a second per new backend; a parallel run
    /// then fails hundreds of tests on timeouts, and even one class exceeds the hang limit. A server without pgvector fails
    /// migration 2026100520 in every fixture. Fail once, with the fix.
    /// </summary>
    private static async Task CheckServerAsync()
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT version(), EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector')", connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        if (reader.GetString(0).Contains("windows", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ATLAS_TEST_CONNECTION points at a native Windows PostgreSQL, which is too slow for "
                + "database-per-test fixtures. Start the Podman test database from README.md (\"Testy\") and point ATLAS_TEST_CONNECTION at it.");
        }
        if (!reader.GetBoolean(1))
        {
            throw new InvalidOperationException("ATLAS_TEST_CONNECTION points at a PostgreSQL without pgvector, which migration 2026100520 "
                + "requires. Remove the old atlas-test-pg container and start it again with the pgvector image from AGENTS.md (\"Testing\").");
        }
        await reader.DisposeAsync();
        await RelaxDurabilityAsync(connection);
        await SweepDeadRunsAsync(connection);
    }

    /// <summary>
    /// With fsync on, every commit of every test waits for the disk and every DROP DATABASE forces a checkpoint that the other
    /// creates and drops queue behind: the suite took twice as long (measured 2026-11-13: 1400 s against 764 s for the CI batches),
    /// and on a workstation with many cores the queued connections reached the server's limit and failed tests at random. A test
    /// server need not survive a crash. The container from AGENTS.md starts without fsync; a server started without those settings
    /// (the CI job's service container) is switched by the run itself when ATLAS_TEST_DISPOSABLE_SERVER=1 says it may be, and
    /// otherwise refused with the fix. synchronous_commit stays at its default (a test asserts it).
    /// </summary>
    private static async Task RelaxDurabilityAsync(NpgsqlConnection connection)
    {
        await using var show = new NpgsqlCommand("SELECT current_setting('fsync')", connection);
        if ((string)(await show.ExecuteScalarAsync())! == "off")
        {
            return;
        }
        if (Environment.GetEnvironmentVariable("ATLAS_TEST_DISPOSABLE_SERVER") != "1")
        {
            throw new InvalidOperationException("ATLAS_TEST_CONNECTION points at a PostgreSQL with fsync on, where the suite takes twice as long "
                + "and fails at random under parallel load. Start the test database as in AGENTS.md (\"Testing\": -c fsync=off -c full_page_writes=off), "
                + "or set ATLAS_TEST_DISPOSABLE_SERVER=1 to let the run switch them off on this server (ALTER SYSTEM; only for a server that holds nothing else).");
        }
        foreach (var sql in new[] { "ALTER SYSTEM SET fsync = off", "ALTER SYSTEM SET full_page_writes = off", "SELECT pg_reload_conf()" })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// A run that is killed (a hang abort, a cancelled build, a closed terminal) never reaches its ProcessExit drop, and its databases
    /// stayed on the server for good (dozens per run, ~11 MB each). Drops those of this machine's runs whose process no longer exists;
    /// other machines' runs, and runs whose process id is alive again, are left alone.
    /// </summary>
    private static async Task SweepDeadRunsAsync(NpgsqlConnection connection)
    {
        var prefix = "atlas_t" + s_machine + "_";
        var databases = new List<string>();
        await using (var list = new NpgsqlCommand("SELECT datname FROM pg_database WHERE starts_with(datname, @prefix)", connection))
        {
            list.Parameters.AddWithValue("prefix", prefix);
            await using var names = await list.ExecuteReaderAsync();
            while (await names.ReadAsync())
            {
                databases.Add(names.GetString(0));
            }
        }
        foreach (var database in databases)
        {
            if (!int.TryParse(database[prefix.Length..].Split('_')[0], out var pid) || IsRunning(pid))
            {
                continue;
            }
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", connection) { CommandTimeout = 0 };
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // Unpooled: nothing may stay connected to the template, and fixtures opt in to pooling themselves.
    private static string For(string database) => new NpgsqlConnectionStringBuilder(Admin) { Database = database, Pooling = false }.ConnectionString;

    /// <summary>
    /// Every fixture opens one-use connections to its fresh database. Two suites at once on one Windows machine (a
    /// developer run beside an agent's) can use up the client's ephemeral ports with sockets in TIME_WAIT, and Windows
    /// then refuses new connections with WSAEADDRINUSE until some expire: wait for them instead of failing the test.
    /// </summary>
    private static async Task OpenAsync(NpgsqlConnection connection)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await connection.OpenAsync();
                return;
            }
            catch (NpgsqlException error) when (attempt < 10 && error.InnerException is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
            }
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await OpenAsync(connection);
        // No command timeout: CREATE/DROP DATABASE can queue behind checkpoints while dozens of fixtures start and end;
        // the run's blame hang timeout still ends a real hang.
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync();
    }

    private static void DropLeftovers()
    {
        try
        {
            using var connection = new NpgsqlConnection(Admin);
            connection.Open();
            foreach (var database in s_live.Keys)
            {
                using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", connection);
                command.ExecuteNonQuery();
            }
        }
        catch (NpgsqlException)
        {
            // Best effort at exit: the next run uses fresh names, so a leftover never affects a result.
        }
    }
}
