using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.ModuleSettings;
using Xunit;

namespace Atlas.Server.Tests.Platform;

/// <summary>
/// The runtime database role (RuntimeDatabaseRole): what the owner grants it at each start holds in PostgreSQL itself, whatever SQL reaches
/// the database as that role. The test server's role creates a role of its own per test (roles belong to the cluster) and drops it.
/// </summary>
public sealed class RuntimeDatabaseRoleTests
{
    [PostgreSqlFact]
    public async Task The_runtime_role_appends_audit_facts_closes_history_and_changes_nothing_else_of_them()
    {
        var schema = await DisposableSchema.CreateMigratedAsync("atlas_runtime_role_");
        var role = "atlas_app_" + Guid.NewGuid().ToString("N")[..12];
        var runtime = new NpgsqlConnectionStringBuilder(schema.ConnectionString) { Username = role, Password = "runtime-" + Guid.NewGuid().ToString("N"), Pooling = false }.ConnectionString;
        using var database = new AtlasDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:atlas-db"] = runtime, [$"ConnectionStrings:{RuntimeDatabaseRole.OwnerConnectionName}"] = schema.ConnectionString,
        }).Build(), NullLogger<AtlasDatabase>.Instance);
        try
        {
            // The start migrates as the owner and creates the role; the next start keeps it as it is.
            await database.InitializeAsync(default);
            await RuntimeDatabaseRole.EnsureAsync(schema.ConnectionString, runtime, default);
            var user = Guid.NewGuid();
            await ExecuteAsync(schema.ConnectionString, """
                INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES (@user,'owner','O',false,true,'s');
                INSERT INTO "UserAccountRevision"("Id","UserId","Username","DisplayName","IsActive","IsWikiAdmin","ActiveFrom")
                  VALUES (gen_random_uuid(),@user,'owner','O',true,false,now() - interval '1 day');
                INSERT INTO "UserGroup"("Id","Name") VALUES (@user,'Skupina');
                INSERT INTO "GroupMember"("Id","GroupId","UserId","ActiveFrom") VALUES (@user,@user,@user,now() - interval '1 day');
                """, ("user", user));

            // The application closes an interval through its session: the update names "ActiveTo" alone (DynamicUpdate).
            using (var session = database.OpenSession())
            using (var transaction = session.BeginTransaction())
            {
                (await session.GetAsync<GroupMember>(user)).ActiveTo = DateTime.UtcNow;
                await transaction.CommitAsync();
            }

            // An audit fact is appended and read, never changed or removed.
            await ExecuteAsync(runtime, """INSERT INTO "RevokedBrowserSession"("Id","UserId") VALUES (gen_random_uuid(), @user)""", ("user", user));
            Assert.Equal(1L, await ScalarAsync(runtime, """SELECT count(*) FROM "RevokedBrowserSession" """));
            foreach (var rewrite in new[] { """UPDATE "RevokedBrowserSession" SET "RevokedAt" = now()""", """DELETE FROM "RevokedBrowserSession" """, """TRUNCATE "RevokedBrowserSession" """ })
            {
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(runtime, rewrite))).SqlState);
            }
            // An interval of access history closes, and nothing else of it changes.
            await ExecuteAsync(runtime, """UPDATE "UserAccountRevision" SET "ActiveTo" = now() WHERE "UserId" = @user""", ("user", user));
            foreach (var rewrite in new[] { """UPDATE "UserAccountRevision" SET "DisplayName" = 'X' WHERE "UserId" = @user""", """DELETE FROM "UserAccountRevision" WHERE "UserId" = @user""" })
            {
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(runtime, rewrite, ("user", user)))).SqlState);
            }
            // Other rows as before; the role is a plain login.
            await ExecuteAsync(runtime, """UPDATE "UserAccount" SET "DisplayName" = 'Owner' WHERE "Id" = @user""", ("user", user));
            Assert.Equal(false, await ScalarAsync(schema.ConnectionString, "SELECT rolsuper OR rolcreatedb OR rolcreaterole FROM pg_roles WHERE rolname = @role", ("role", role)));
            // The application's sessions are the runtime role's; the restore's is the owner's, which may empty the table.
            using (var session = database.OpenSession())
            {
                var refused = await Assert.ThrowsAnyAsync<Exception>(() => session.CreateSQLQuery("""DELETE FROM "RevokedBrowserSession" """).ExecuteUpdateAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(refused.InnerException).SqlState);
            }
            var (owned, connection) = await database.OpenOwnerSessionAsync(default);
            await using (connection)
            using (owned)
            using (var transaction = owned.BeginTransaction())
            {
                Assert.Equal(1, await owned.CreateSQLQuery("""DELETE FROM "RevokedBrowserSession" """).ExecuteUpdateAsync());
                await transaction.RollbackAsync();
            }
        }
        finally
        {
            await schema.DisposeAsync();
            await ExecuteAsync(new NpgsqlConnectionStringBuilder(schema.ConnectionString) { Database = "postgres", SearchPath = null }.ConnectionString, $"DROP ROLE IF EXISTS \"{role}\"");
        }
    }

    /// <summary>NHibernate writes every mapped column of an update unless the entity updates dynamically; on an interval table the
    /// runtime role may update "ActiveTo" alone, so closing an interval would be refused.</summary>
    [Fact]
    public void Every_entity_on_an_interval_table_updates_only_the_columns_it_changed()
    {
        var classes = AtlasMappings.Create().CompileMappingForAllExplicitlyAddedEntities().RootClasses
            .Where(type => RuntimeDatabaseRole.Intervals.Contains(type.table?.Trim('`'))).ToList();
        Assert.Contains(classes, type => type.table == "`GroupMember`");
        Assert.Empty(classes.Where(type => !type.dynamicupdate).Select(type => type.Name));
    }

    /// <summary>The start prepares the role before the first login as it (the OIDC settings loader): one that does not exist yet, and
    /// one whose password the connection string changed. Without it the loader is refused, and with RequireDatabaseAtStartup the start fails.</summary>
    [PostgreSqlFact]
    public async Task The_start_prepares_a_missing_role_and_a_changed_password_before_the_first_login()
    {
        var schema = await DisposableSchema.CreateMigratedAsync("atlas_runtime_start_");
        var role = "atlas_app_" + Guid.NewGuid().ToString("N")[..12];
        var root = Directory.CreateTempSubdirectory("atlas-runtime-role-").FullName;
        IConfiguration Configuration(string password) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:atlas-db"] = new NpgsqlConnectionStringBuilder(schema.ConnectionString) { Username = role, Password = password, Pooling = false }.ConnectionString,
            [$"ConnectionStrings:{RuntimeDatabaseRole.OwnerConnectionName}"] = schema.ConnectionString,
            ["Authentication:OpenIdConnect:RequireDatabaseAtStartup"] = "true",
        }).Build();
        var environment = new AttachmentTests.EnvironmentStub(root);
        try
        {
            var first = Configuration("first-" + Guid.NewGuid().ToString("N"));
            await Assert.ThrowsAsync<PostgresException>(() => OidcRuntimeLoader.LoadAsync(first, environment));
            await RuntimeDatabaseRole.PrepareAsync(first, NullLogger.Instance);
            await OidcRuntimeLoader.LoadAsync(first, environment);

            var changed = Configuration("second-" + Guid.NewGuid().ToString("N"));
            await Assert.ThrowsAsync<PostgresException>(() => OidcRuntimeLoader.LoadAsync(changed, environment));
            await RuntimeDatabaseRole.PrepareAsync(changed, NullLogger.Instance);
            await OidcRuntimeLoader.LoadAsync(changed, environment);
        }
        finally
        {
            await schema.DisposeAsync();
            await ExecuteAsync(new NpgsqlConnectionStringBuilder(schema.ConnectionString) { Database = "postgres", SearchPath = null }.ConnectionString, $"DROP ROLE IF EXISTS \"{role}\"");
            Directory.Delete(root, true);
        }
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteScalarAsync();
    }

    private static Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters) => ScalarAsync(connectionString, sql, parameters);
}
