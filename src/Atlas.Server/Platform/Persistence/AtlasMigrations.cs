using System.Data;
using System.Reflection;
using Atlas.Server.Platform.Modules;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Initialization;
using FluentMigrator.Runner.Processors.Postgres;
using FluentMigrator.Runner.VersionTableInfo;

namespace Atlas.Server.Platform.Persistence;

/// <summary>Runs the application's known migrations under one PostgreSQL transaction and lock.</summary>
public static class AtlasMigrations
{
    public const string JournalSql = """
        CREATE TABLE IF NOT EXISTS "SchemaVersion" (
            "Version" bigint PRIMARY KEY,
            "AppliedAt" timestamptz NOT NULL DEFAULT now(),
            "Description" varchar(1024) NULL);
        ALTER TABLE "SchemaVersion" ADD COLUMN IF NOT EXISTS "Description" varchar(1024) NULL;
        """;

    /// <summary>The platform and every module of the distribution, enabled or not (Atlas 3 plan 5.4): a database where a module
    /// once ran still starts (an applied migration nobody knows is refused below), and every module's tables always exist.</summary>
    internal static Assembly[] Assemblies { get; } =
        AtlasModules.All.Select(module => module.MigrationsAssembly).Prepend(typeof(AtlasMigrations).Assembly).Distinct().ToArray();

    /// <summary>FluentMigrator is synchronous and waits on the database; it runs on its own thread, never holding a pool thread
    /// (test processes start dozens of hosts at once, each migrating).</summary>
    public static Task MigrateAsync(string connectionString, CancellationToken ct = default)
        => Task.Factory.StartNew(() => Migrate(connectionString, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void Migrate(string connectionString, CancellationToken ct)
    {
        var metadata = new AtlasVersionTable();
        using var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .WithGlobalCommandTimeout(TimeSpan.FromSeconds(90))
                .ScanIn(Assemblies).For.Migrations().For.EmbeddedResources())
            .Configure<RunnerOptions>(options => options.TransactionPerSession = true)
            .Configure<PostgresOptions>(options => options.ForceQuote = true)
            .AddSingleton<IVersionTableMetaData>(metadata)
            .BuildServiceProvider();
        using var serviceScope = services.CreateScope();
        var runner = serviceScope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        // Begin before version loading: journal adoption and every Up method share this transaction.
        using var transaction = runner.BeginScope();
        try
        {
            ct.ThrowIfCancellationRequested();
            runner.Processor.Execute("SET LOCAL TIME ZONE 'UTC'; SELECT pg_advisory_xact_lock(731145010)");
            metadata.SchemaName = (string)runner.Processor.Read("SELECT current_schema()").Tables[0].Rows[0][0];
            var quotedSchema = "\"" + metadata.SchemaName.Replace("\"", "\"\"") + "\"";
            runner.Processor.Execute(JournalSql.Replace("\"SchemaVersion\"", quotedSchema + ".\"SchemaVersion\""));
            var migrations = runner.MigrationLoader.LoadMigrations();
            var known = migrations.Keys.ToHashSet();
            var applied = runner.Processor.Read("SELECT \"Version\" FROM \"SchemaVersion\"")
                .Tables[0].Rows.Cast<DataRow>().Select(row => Convert.ToInt64(row[0])).ToArray();
            if (applied.Any(version => !known.Contains(version)))
                throw new ApiException(503, "Databáze obsahuje migraci neznámou této verzi aplikace.");
            ct.ThrowIfCancellationRequested();
            // FM 8.0.1 MigrateUp validates by rolling back an existing transaction.
            // Apply the same versioned migration infos through its public runner API instead.
            foreach (var migration in migrations.Values.Where(m => !applied.Contains(m.Version)))
            {
                ct.ThrowIfCancellationRequested();
                ((MigrationRunner)runner).ApplyMigrationUp(migration, true);
            }
            ct.ThrowIfCancellationRequested();
            transaction.Complete();
        }
        catch
        {
            if (transaction.IsActive)
            {
                transaction.Cancel();
            }
            throw;
        }
    }

    private sealed class AtlasVersionTable : IVersionTableMetaData
    {
        public bool OwnsSchema => false;
        public string SchemaName { get; set; } = "";
        public string TableName => "SchemaVersion";
        public string ColumnName => "Version";
        public string AppliedOnColumnName => "AppliedAt";
        public string DescriptionColumnName => "Description";
        public string UniqueIndexName => "SchemaVersion_pkey";
        public bool CreateWithPrimaryKey => true;
    }
}
