using System.Diagnostics;
using Atlas.Server.Features.AppLogs;
using Atlas.Server.Features.Backups.Installation;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Logs;
using Atlas.Server.Platform.Persistence;
using Atlas.Server.Platform.Retention;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Hub;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Atlas.Server.Tests.AppLogs;

/// <summary>Systémový log (3.6): the provider queues without waiting, the writer stores redacted and capped batches, the retention ages and
/// counts the log, and a wiki administrator (a Hub one too) pages and filters it with one statement a page.</summary>
public sealed class AppLogTests(ITestOutputHelper output)
{
    [Fact]
    public void Ten_thousand_log_calls_only_queue_and_a_full_queue_drops_and_counts()
    {
        var options = new AppLogOptions { Capacity = 10_000 };
        var queue = new AppLogQueue(options);
        using var provider = new AppLogProvider(queue, options, new HttpContextAccessor());
        var logger = provider.CreateLogger("Atlas.Server.Features.Test");
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
        {
            logger.LogInformation("Line {Number} of {Total}", i, 10_000);
        }
        watch.Stop();
        output.WriteLine($"10 000 calls: {watch.Elapsed.TotalMilliseconds:F1} ms");
        // No database on the calling thread: the provider has none, and the lines wait in the queue.
        Assert.Equal(10_000, queue.Reader.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"10 000 log calls took {watch.Elapsed}");

        logger.LogWarning("Past the capacity");
        logger.LogError("Past the capacity again");
        Assert.Equal(2, queue.Dropped);
        Assert.Equal(10_000, queue.Reader.Count);
    }

    [Fact]
    public void The_minimum_level_follows_the_longest_category_prefix_and_configuration_overrides_a_default()
    {
        var options = new AppLogOptions();
        Assert.Equal(LogLevel.Information, options.MinimumFor("Atlas.Server.Features.Backups.BackupJob"));
        Assert.Equal(LogLevel.Warning, options.MinimumFor("Microsoft.AspNetCore.Routing.EndpointMiddleware"));
        Assert.Equal(LogLevel.Information, options.MinimumFor("Microsoft.Hosting.Lifetime"));
        Assert.Equal(LogLevel.Warning, options.MinimumFor("NHibernate.SQL"));
        Assert.Equal(LogLevel.Information, options.MinimumFor("NHibernateX"));
        Assert.Equal(LogLevel.None, options.MinimumFor(typeof(AppLogWriter).FullName!));

        var configured = AppLogOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppLog:MinimumLevel"] = "Warning", ["AppLog:Categories:NHibernate"] = "Debug",
        }).Build());
        Assert.Equal(LogLevel.Warning, configured.MinimumFor("Atlas.Server.Anything"));
        Assert.Equal(LogLevel.Debug, configured.MinimumFor("NHibernate.SQL"));
        Assert.Equal(LogLevel.Warning, configured.MinimumFor("Wolverine.Runtime"));
    }

    [Theory]
    [InlineData("Host=db;Username=atlas;Password=Tajne-123;Database=atlas", "Host=db;Username=atlas;Password=***;Database=atlas")]
    [InlineData("Authorization: Bearer eyAbc.def-ghi_jkl", "Authorization: *** ***")]
    [InlineData("GET /cb?code=1&access_token=abcdef&state=x", "GET /cb?code=1&access_token=***&state=x")]
    [InlineData("{\"clientSecret\":\"s3cr3t\",\"name\":\"x\"}", "{\"clientSecret\":***,\"name\":\"x\"}")]
    [InlineData("api_key='x y z' next", "api_key=*** next")]
    [InlineData("https://bob:hunter2@git.example.com/repo.git", "https://***@git.example.com/repo.git")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abcdefgh end", "jwt *** end")]
    [InlineData("token sat_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef used", "token *** used")]
    [InlineData("Basic dXNlcjpwYXNzd29yZA==", "Basic ***")]
    [InlineData("Page 1f0c saved by Jana, 3 tokens used", "Page 1f0c saved by Jana, 3 tokens used")]
    public void Secrets_are_redacted_and_ordinary_text_stays(string text, string expected) => Assert.Equal(expected, AppLogRedaction.Redact(text));

    [Fact]
    public void The_log_is_registered_for_retention_and_kept_out_of_backups()
    {
        var log = OperationalRetention.Logs.Single(log => log.Name == "AppLog");
        Assert.Equal(("AppLog", LogClass.Required, "At", 14, 200_000L, "Id"), (log.Table, log.Class, log.Time, log.Days, log.MaxRows, log.Order));
        Assert.Contains("AppLog", BackupCoverage.Operational);
        Assert.True(BackupCoverage.Running("AppLog"));
    }

    /// <summary>Migration 2026110667 over an installation: the table comes empty and takes a line; an unknown level is refused.</summary>
    [PostgreSqlFact]
    public async Task The_migration_adds_an_empty_log_beside_existing_rows()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_app_log_migration_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110667);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('00000000-0000-0000-0000-000000000001','admin','Admin',true,true,'stamp');
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        await ExecuteAsync(schema.ConnectionString, """INSERT INTO "AppLog"("At","Level","Category","Message") VALUES(now(),4,'Atlas.Test','Line')""");
        await using var connection = new NpgsqlConnection(schema.ConnectionString);
        await connection.OpenAsync();
        await using (var count = new NpgsqlCommand("""SELECT count(*) || '|' || (SELECT count(*) FROM "UserAccount") FROM "AppLog" """, connection))
        {
            Assert.Equal("1|1", await count.ExecuteScalarAsync());
        }
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString, """INSERT INTO "AppLog"("At","Level","Category","Message") VALUES(now(),9,'x','y')"""));
    }

    [PostgreSqlFact]
    public async Task A_wiki_administrator_pages_and_filters_the_stored_log_and_nobody_else_reads_it()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_app_log_") /* own database: seeds the installation's one Hub connection (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and reads the whole log (its summary counts every line of AppLog) */;
        var queue = new AppLogQueue(new AppLogOptions());
        var writer = new AppLogWriter(queue, host.Database, host.Services.GetRequiredService<ILogger<AppLogWriter>>());
        var at = DateTime.UtcNow.AddMinutes(-30);
        Exception thrown;
        try
        {
            throw new InvalidOperationException("Connection failed: Password=Tajne-123;Host=db");
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }
        queue.Write(new AppLogEntry(at.AddHours(-2), LogLevel.Information, "Atlas.Server.Features.Backups.BackupJob", null, "Old line", null, null, null));
        queue.Write(new AppLogEntry(at, LogLevel.Information, "Atlas.Server.Features.Pages.PageHandler", 7, "Saved with\0 a NUL " + new string('x', 5000), null, "trace-1", null));
        queue.Write(new AppLogEntry(at, LogLevel.Warning, "Atlas.Server.Features.Pages.PageHandler", null, "Slow save", null, "trace-1", null));
        queue.Write(new AppLogEntry(at, LogLevel.Error, "Atlas.Server.Features.Backups.BackupJob", null, "Backup failed", thrown, "trace-2", Guid.Empty));
        queue.Write(new AppLogEntry(at, LogLevel.Debug, "Atlas.Server.Features.Pages.PageHandler", null, "Detail", null, null, null));
        await writer.FlushAsync(default);
        Assert.Equal(0, queue.Reader.Count);

        // A Hub administrator is an effective wiki administrator (3.5.2 #7).
        var (hubAdmin, _, _) = await HubEffectiveAuthorityTests.SeedAsync(host, true);
        Assert.False(hubAdmin.IsWikiAdmin);
        host.Actor.Current = hubAdmin;
        var first = await host.InvokeAsync<AppLogPage>(new ListAppLog(new AppLogFilter(), Take: 2));
        Assert.Equal(new[] { "Detail", "Backup failed" }, first.Entries.Select(entry => entry.Message));
        Assert.True(first.More);
        Assert.Equal(new AppLogSummary(1, 1, 1, 1, 0), first.Summary);
        var error = first.Entries[1];
        Assert.Equal(("error", "System.InvalidOperationException", "trace-2", Guid.Empty), (error.Level, error.ExceptionType, error.TraceId, error.UserId));
        Assert.Equal("Connection failed: Password=***;Host=db", error.ExceptionMessage);
        Assert.Contains("A_wiki_administrator_pages_and_filters", error.Exception);
        Assert.DoesNotContain("Tajne", error.Exception);

        // The cursor: older pages, then the end.
        var second = await host.InvokeAsync<AppLogPage>(new ListAppLog(new AppLogFilter(), first.Entries[^1].Id, Take: 2));
        Assert.Equal(new[] { "Slow save", "Saved with" }, second.Entries.Select(entry => entry.Message[..Math.Min(10, entry.Message.Length)]));
        Assert.Null(second.Summary);
        var saved = second.Entries[1];
        Assert.Equal((AppLogWriter.MaxMessage, 7), (saved.Message.Length, saved.EventId));
        Assert.DoesNotContain('\0', saved.Message);
        var last = await host.InvokeAsync<AppLogPage>(new ListAppLog(new AppLogFilter(), second.Entries[^1].Id, Take: 2));
        Assert.Equal(new[] { "Old line" }, last.Entries.Select(entry => entry.Message));
        Assert.False(last.More);

        // The live tail: lines above the newest one held; more of them than a page is a gap.
        var newest = first.Entries[0].Id;
        Assert.Empty((await host.InvokeAsync<AppLogPage>(new ListAppLog(new AppLogFilter(), After: newest))).Entries);
        for (var i = 0; i < 3; i++)
        {
            queue.Write(new AppLogEntry(DateTime.UtcNow, LogLevel.Information, "Atlas.Test", null, "New " + i, null, null, null));
        }
        await writer.FlushAsync(default);
        var tail = await host.InvokeAsync<AppLogPage>(new ListAppLog(new AppLogFilter(), After: newest, Take: 2));
        Assert.Equal(new[] { "New 2", "New 1" }, tail.Entries.Select(entry => entry.Message));
        Assert.True(tail.More);

        // The filters.
        async Task<string[]> MessagesAsync(AppLogFilter filter) =>
            (await host.InvokeAsync<AppLogPage>(new ListAppLog(filter))).Entries.Select(entry => entry.Message[..Math.Min(10, entry.Message.Length)]).ToArray();
        Assert.Equal(new[] { "Backup fai", "Slow save" }, await MessagesAsync(new AppLogFilter(Level: "warning")));
        Assert.Equal(new[] { "Backup fai", "Old line" }, await MessagesAsync(new AppLogFilter(Category: "Atlas.Server.Features.Backups")));
        Assert.Empty(await MessagesAsync(new AppLogFilter(Category: "Atlas.Server.Features.Backups%")));
        Assert.Equal(new[] { "Backup fai" }, await MessagesAsync(new AppLogFilter(Q: "connection FAILED")));
        Assert.Equal(new[] { "Slow save", "Saved with" }, await MessagesAsync(new AppLogFilter(TraceId: "trace-1")));
        Assert.Equal(new[] { "Backup fai" }, await MessagesAsync(new AppLogFilter(Exception: true)));
        Assert.Equal(new[] { "Old line" }, await MessagesAsync(new AppLogFilter(To: at.AddHours(-1))));
        Assert.Equal(new[] { "Detail", "Backup fai", "Slow save", "Saved with" }, await MessagesAsync(new AppLogFilter(From: at.AddMinutes(-1), To: at.AddMinutes(1))));
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => MessagesAsync(new AppLogFilter(Level: "loud")))).Status);

        // One statement a page and one for the summary, however long the log.
        async Task<IReadOnlyList<string>> StatementsAsync()
        {
            using var recording = SqlRecording.Start();
            await host.InvokeAsync<AppLogPage>(new ListAppLog(new AppLogFilter(Level: "information", Q: "e")));
            return recording.Statements;
        }
        var few = await StatementsAsync();
        await host.SeedAsync(session => session.CreateSQLQuery("""
            INSERT INTO "AppLog"("At","Level","Category","Message") SELECT now() - n * interval '1 second', n % 5, 'Atlas.Seed', 'Seeded ' || n FROM generate_series(1, 2000) n
            """).ExecuteUpdateAsync());
        var many = await StatementsAsync();
        output.WriteLine($"{many.Count} statements:\n{string.Join("\n", many)}");
        Assert.Equal(few, many);

        // Not for anyone else: a space administrator and a reader are refused.
        var spaceAdmin = await host.SeedUserAsync("spravce");
        await host.SeedSpaceAsync("Prostor", (spaceAdmin, SpaceRole.Admin));
        host.Actor.Current = spaceAdmin;
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<AppLogPage>(new ListAppLog(new AppLogFilter())))).Status);
    }

    [PostgreSqlFact]
    public async Task The_retention_ages_the_log_and_keeps_its_newest_rows_up_to_the_count()
    {
        var options = new RetentionOptions();
        options.Logs["AppLog"] = new RetentionLimit { MaxRows = 3 };
        await using var host = await AtlasTestHost.CreateAsync("atlas_app_log_retention_", services => services.AddSingleton(options));
        await host.SeedAsync(session => session.CreateSQLQuery("""
            INSERT INTO "AppLog"("At","Level","Category","Message") VALUES (now() - interval '15 days',4,'Atlas.Old','Too old');
            INSERT INTO "AppLog"("At","Level","Category","Message") SELECT now() - n * interval '1 minute', 2, 'Atlas.Seed', 'Line ' || n FROM generate_series(1, 5) n;
            """).ExecuteUpdateAsync());

        var deleted = await host.Services.GetRequiredService<OperationalRetention>().RunAsync(default);

        Assert.Equal(3, deleted["AppLog"]);
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync();
        await using var rows = new NpgsqlCommand("""SELECT string_agg("Message", ',' ORDER BY "Id") FROM "AppLog" """, connection);
        Assert.Equal("Line 3,Line 4,Line 5", await rows.ExecuteScalarAsync());
    }

    [Fact]
    public void A_line_logged_in_a_job_run_scope_carries_the_run_whatever_logger_writes_it()
    {
        var options = new AppLogOptions();
        var queue = new AppLogQueue(options);
        using var factory = LoggerFactory.Create(logging => logging.AddProvider(new AppLogProvider(queue, options, new HttpContextAccessor())));
        var logger = factory.CreateLogger("Atlas.Test");
        logger.LogInformation("Outside");
        using (logger.BeginScope(new Dictionary<string, object> { [JobLogScope.RunId] = 42L, [JobLogScope.Name] = "Záloha" }))
        using (logger.BeginScope("an inner scope of another kind"))
        {
            factory.CreateLogger("Atlas.Other").LogWarning("Inside, another logger");
        }
        logger.LogInformation("After");
        var entries = new List<AppLogEntry>();
        while (queue.Reader.TryRead(out var entry))
        {
            entries.Add(entry);
        }
        Assert.Equal(new long?[] { null, 42, null }, entries.Select(entry => entry.JobRunId));
    }

    /// <summary>The runner runs every attempt in the run's scope: the lines of the job and of the services it calls are the run's, and the
    /// filter by run finds them with the job they belong to.</summary>
    [PostgreSqlFact]
    public async Task A_job_runs_lines_are_found_by_its_run()
    {
        var options = new AppLogOptions();
        var queue = new AppLogQueue(options);
        await using var host = await AtlasTestHost.CreateAsync("atlas_app_log_job_", services =>
        {
            services.AddSingleton<ILoggerProvider>(new AppLogProvider(queue, options, new HttpContextAccessor()));
            services.AddJob<LoggingJob>();
        });
        var writer = new AppLogWriter(queue, host.Database, host.Services.GetRequiredService<ILogger<AppLogWriter>>());
        host.Services.GetRequiredService<ILogger<AppLogTests>>().LogInformation("Before the run");
        var run = await host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(LoggingJob.JobId, "test");
        await writer.FlushAsync(default);

        host.Actor.Current = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var page = await host.InvokeAsync<AppLogPage>(new ListAppLog(new AppLogFilter(JobRunId: run)));
        Assert.Equal(new[] { "A service called by the job", "The job's own line" }, page.Entries.Select(entry => entry.Message));
        Assert.All(page.Entries, entry => Assert.Equal((run, LoggingJob.JobId, "Logující úloha"), (entry.JobRunId!.Value, entry.JobId, entry.JobTitle)));
        var all = await host.InvokeAsync<AppLogPage>(new ListAppLog(new AppLogFilter(Q: "Before the run")));
        Assert.Null(Assert.Single(all.Entries).JobRunId);
    }

    public sealed class LoggingJob : IJobDefinition
    {
        public const string JobId = "test.logging";
        public string Id => JobId;
        public string Title => "Logující úloha";
        public string Description => "Zapíše do logu dva řádky.";
        public JobSchedule? Schedule => null;

        public async Task RunAsync(JobContext context, CancellationToken ct)
        {
            context.Log.LogInformation("The job's own line");
            await Task.Yield();
            context.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Atlas.Server.Features.Other").LogWarning("A service called by the job");
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
