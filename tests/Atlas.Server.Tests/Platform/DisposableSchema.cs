using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Atlas.Server.Tests.Platform;

/// <summary>A schema in its own disposable database (see <see cref="TestDatabases"/>); disposing drops the database.</summary>
internal sealed class DisposableSchema : IAsyncDisposable
{
    private DisposableSchema(string databaseConnection, string schema, bool pooling)
    {
        // Pooled by default, like production: a new PostgreSQL backend costs about a second on Windows. The pool
        // cannot outlive the fixture and hold idle backends, because disposing clears it and drops the database.
        ConnectionString = new NpgsqlConnectionStringBuilder(databaseConnection) { SearchPath = schema + ",public", Pooling = pooling }.ConnectionString;
        Configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:atlas-db"] = ConnectionString })
            .Build();
    }

    public string ConnectionString { get; }
    public IConfiguration Configuration { get; }

    /// <summary>An empty schema, for tests that apply migrations themselves or stop at an earlier one.</summary>
    public static async Task<DisposableSchema> CreateAsync(string prefix, bool pooling = true)
    {
        var schema = prefix + Guid.NewGuid().ToString("N");
        return new DisposableSchema(await TestDatabases.CreateEmptyAsync(schema), schema, pooling);
    }

    /// <summary>A schema already migrated to head, cloned from the run's template; hosts start without migrating.</summary>
    public static async Task<DisposableSchema> CreateMigratedAsync(string prefix, bool pooling = true)
    {
        var schema = prefix + Guid.NewGuid().ToString("N");
        return new DisposableSchema(await TestDatabases.CreateMigratedAsync(schema), schema, pooling);
    }

    /// <summary>An existing database's public schema (a scratch copy the caller made and drops itself): disposing leaves it.</summary>
    public static DisposableSchema Existing(string connectionString) => new(connectionString, "public", false) { m_owned = false };

    private bool m_owned = true;

    public ValueTask DisposeAsync() => m_owned ? new(TestDatabases.DropAsync(ConnectionString)) : ValueTask.CompletedTask;
}
