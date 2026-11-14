using FluentMigrator.Runner;
using FluentMigrator.Runner.Initialization;
using FluentMigrator.Runner.Logging;
using FluentMigrator.Runner.Processors;
using FluentMigrator.Runner.Processors.Postgres;

namespace Atlas.Server.Platform.Persistence;

public static class AtlasMigrationScript
{
    /// <summary>Exports a fresh-schema script without reading configuration or connecting to a database.</summary>
    public static string Generate()
    {
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        output.WriteLine("-- Atlas: fresh application schema only; PostgreSQL database must already exist.");
        output.WriteLine("-- Run with psql -v ON_ERROR_STOP=1 -f schema.sql against an empty application schema.");
        output.WriteLine("BEGIN;");
        output.WriteLine("SET LOCAL TIME ZONE 'UTC';");
        output.WriteLine("SELECT pg_advisory_xact_lock(731145010);");
        output.WriteLine(AtlasMigrations.JournalSql);

        using var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(runner => runner.AddPostgres()
                .ScanIn(AtlasMigrations.Assemblies).For.Migrations().For.EmbeddedResources())
            .Configure<RunnerOptions>(options => options.NoConnection = true)
            .Configure<ProcessorOptions>(options => options.PreviewOnly = true)
            .Configure<PostgresOptions>(options => options.ForceQuote = true)
            .AddLogging(logging => logging.AddProvider(new SqlScriptFluentMigratorLoggerProvider(
                output, new SqlScriptFluentMigratorLoggerOptions
                {
                    OutputGoBetweenStatements = false,
                    OutputSemicolonDelimiter = true
                }, false)))
            .BuildServiceProvider();
        using var serviceScope = services.CreateScope();
        var runner = serviceScope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        foreach (var migration in runner.MigrationLoader.LoadMigrations().Values)
        {
            runner.Up(migration.Migration);
            var description = (migration.Description ?? migration.Migration.GetType().Name).Replace("'", "''");
            output.WriteLine($"INSERT INTO \"SchemaVersion\"(\"Version\",\"Description\") VALUES ({migration.Version},'{description}');");
        }
        output.WriteLine("COMMIT;");
        return output.ToString();
    }
}
