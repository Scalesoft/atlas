using System.Diagnostics;
using Atlas.Server.Features.Notifications.Contracts;
using Atlas.Server.Features.Notifications.Queries;
using Atlas.Server.Platform;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Runtime;
using Xunit;
using Xunit.Abstractions;
using static Atlas.Server.Tests.Events.OutboxTests;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Tests.Jobs;

/// <summary>Gate A of the 3.3 plan (§7): Wolverine durable carries the jobs. A1 is in <see cref="JobRuntimeTests"/> (rollback and start
/// after the commit) and measured here; A2 one run per job and four at once; A3 a restart in the middle of a run; A4 a restart in the last
/// attempt and the failure notification; A5 plans across a restart, a stale occurrence, FromData after the caller's commit; A6 the
/// schema the migrations build (2026110401, 2026111300) is exactly Wolverine's; A8 Wolverine's log stays quiet. Every host that registers a
/// job is the test's own: it starts the durable queue and scheduler (or the outbox) over the installation's JobRun, Job and envelope tables.</summary>
public sealed class JobGateATests(ITestOutputHelper output)
{
    [PostgreSqlFact]
    public async Task A1_a_committed_request_starts_without_polling()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_a1_", services => services.AddJob<StepJob>(), durable: true);
        var waits = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var runId = (await host.InvokeAsync<long?>(new RequestProbeJob(StepJob.JobId)))!.Value;
            var committed = DateTime.UtcNow;
            await WaitForAsync(host, runId, JobStates.Succeeded);
            var started = (DateTime)(await ScalarAsync(host.ConnectionString, $"""SELECT "StartedAt" FROM "JobRun" WHERE "Id" = {runId}"""))!;
            waits.Add((started.ToUniversalTime() - committed).TotalMilliseconds);
        }
        output.WriteLine($"A1 start after the commit, ms: {string.Join(", ", waits.Select(wait => wait.ToString("0")))}");
        // Wolverine's scheduled poll is 5 s: anything near it would be polling. The gate's 100 ms is read from the output.
        Assert.All(waits, wait => Assert.True(wait < 1000, $"{wait:0} ms"));
    }

    [PostgreSqlFact]
    public async Task A2_two_runs_of_a_job_never_overlap_and_different_jobs_run_four_at_once()
    {
        string[] jobs = ["test.sleep.1", "test.sleep.2", "test.sleep.3", "test.sleep.4", "test.sleep.5"];
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_a2_", services =>
        {
            foreach (var job in jobs)
            {
                services.AddSingleton<IJobDefinition>(new SleepJob(job));
            }
        }, durable: true);

        await host.InvokeAsync<long?>(new RequestProbeJob(jobs[0], Key: "a"));
        await host.InvokeAsync<long?>(new RequestProbeJob(jobs[0], Key: "b"));
        foreach (var job in jobs.Skip(1))
        {
            await host.InvokeAsync<long?>(new RequestProbeJob(job));
        }
        var clock = Stopwatch.StartNew();
        while ((long)(await ScalarAsync(host.ConnectionString, """SELECT count(*) FROM "JobRun" WHERE "State" = 'succeeded' AND "JobId" LIKE 'test.sleep.%'"""))! < 6)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(40), "Not every run ended.");
            await Task.Delay(100);
        }

        var runs = new List<(string Job, DateTime Start, DateTime End)>();
        using (var session = host.OpenSession())
        {
            // Only this test's jobs: a startup run of another job (contacts.accounts) may still be queued, without a start.
            foreach (var run in (await session.QueryOver<JobRun>().ListAsync()).Where(run => jobs.Contains(run.JobId)))
            {
                runs.Add((run.JobId, run.StartedAt!.Value, run.FinishedAt!.Value));
            }
        }
        var same = runs.Where(run => run.Job == jobs[0]).OrderBy(run => run.Start).ToArray();
        Assert.True(same[0].End <= same[1].Start, "Two runs of one job overlapped.");
        var peak = runs.Max(run => runs.Count(other => other.Start <= run.Start && run.Start < other.End));
        output.WriteLine($"A2 runs at once at most: {peak}");
        Assert.Equal(4, peak);
    }

    [PostgreSqlFact]
    public async Task A3_a_run_stopped_by_a_shutdown_continues_after_the_restart_and_does_no_step_twice()
    {
        var schema = await DisposableSchema.CreateMigratedAsync("atlas_jobs_a3_");
        await ExecuteAsync(schema.ConnectionString, "CREATE TABLE gate_steps (step integer NOT NULL)");
        await using var first = await AtlasTestHost.CreateAsync(schema, services => services.AddJob<GateJob>(), durable: true);
        var runId = (await first.InvokeAsync<long?>(new RequestProbeJob(GateJob.JobId)))!.Value;
        var clock = Stopwatch.StartNew();
        while (await ScalarAsync(schema.ConnectionString, $"""SELECT "Progress"->>'done' FROM "JobRun" WHERE "Id" = {runId}""") is not "1")
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "The first step did not commit.");
            await Task.Delay(50);
        }

        await first.StopAsync();
        Assert.Equal(JobStates.Running, await ScalarAsync(schema.ConnectionString, $"""SELECT "State" FROM "JobRun" WHERE "Id" = {runId}"""));
        await using var second = await AtlasTestHost.CreateAsync(schema, services => services.AddJob<GateJob>(), durable: true);
        await WaitForAsync(second, runId, JobStates.Succeeded);

        Assert.Equal("1,2,3", await ScalarAsync(schema.ConnectionString, "SELECT string_agg(step::text, ',' ORDER BY step) FROM gate_steps"));
        Assert.Equal("2|true", await ScalarAsync(schema.ConnectionString, $"""
            SELECT "Attempt" || '|' || EXISTS (SELECT 1 FROM jsonb_array_elements("Log") e WHERE e->>'message' LIKE 'Server se restartoval během běhu%')
            FROM "JobRun" WHERE "Id" = {runId}
            """));
    }

    [PostgreSqlFact]
    public async Task A4_a_restart_in_the_last_attempt_fails_the_run_and_tells_the_wiki_administrators()
    {
        var schema = await DisposableSchema.CreateMigratedAsync("atlas_jobs_a4_");
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES ('00000000-0000-0000-0000-000000000001','admin','Admin',true,true,'stamp');
            INSERT INTO "JobRun" ("JobId", "Trigger", "State", "Attempt", "StartedAt", "HeartbeatAt")
            VALUES ('{StepJob.JobId}', 'event', 'running', 5, now() - interval '1 hour', now() - interval '1 hour');
            """);

        await using var host = await AtlasTestHost.CreateAsync(schema, services => services.AddJob<StepJob>(), durable: true);
        // This test's run, not a startup run of another job (contacts.accounts) the host queued beside it.
        var runId = (long)(await ScalarAsync(host.ConnectionString, $"""SELECT "Id" FROM "JobRun" WHERE "JobId" = '{StepJob.JobId}'"""))!;
        await WaitForAsync(host, runId, JobStates.Failed);

        Assert.Equal("Server se restartoval během posledního pokusu.|5", await ScalarAsync(host.ConnectionString, $"""
            SELECT "Error" || '|' || "Attempt" FROM "JobRun" WHERE "Id" = {runId}
            """));
        await Events.OutboxTests.DrainAsync(host);
        Assert.Equal("job.failed|test.steps|00000000-0000-0000-0000-000000000001", await ScalarAsync(host.ConnectionString, """
            SELECT "Kind" || '|' || "Anchor" || '|' || "RecipientId" FROM "Notification"
            """));
    }

    [PostgreSqlFact]
    public async Task A4_repeated_failures_of_a_job_are_one_unread_notification_shown_to_wiki_administrators_only()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_notify_", services => services.AddJob<FatalJob>(), outbox: true);
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var runner = host.Services.GetRequiredService<IJobRunner>();
        await runner.RunToEndAsync(FatalJob.JobId, "Test");
        await runner.RunToEndAsync(FatalJob.JobId, "Test");
        await Events.OutboxTests.DrainAsync(host);

        host.Actor.Current = admin;
        var item = Assert.Single((await host.InvokeAsync<NotificationList>(new ListNotifications(null))).Items);
        Assert.Equal(("job.failed", "atlas.job", "Selže hned", "test.fatal", 2, false),
            (item.Kind, item.SubjectType, item.SubjectTitle, item.Anchor, item.Count, item.Read));

        await ExecuteAsync(host.ConnectionString, $"""UPDATE "UserAccount" SET "IsWikiAdmin" = false WHERE "Id" = '{admin.Id}'""");
        Assert.Empty((await host.InvokeAsync<NotificationList>(new ListNotifications(null))).Items);
    }

    [PostgreSqlFact]
    public async Task A5_a_fixed_plan_survives_a_restart_and_an_occurrence_of_an_old_plan_does_nothing()
    {
        var schema = await DisposableSchema.CreateMigratedAsync("atlas_jobs_a5_");
        await using var first = await AtlasTestHost.CreateAsync(schema, services => services.AddJob<HourlyJob>(), durable: true);
        var due = await WaitForPlanAsync(first, HourlyJob.JobId, 1);
        await first.StopAsync();

        await using var second = await AtlasTestHost.CreateAsync(schema, services => services.AddJob<HourlyJob>(), durable: true);
        // The start publishes the plan again; its revision keeps the one plan.
        Assert.Equal(due, await WaitForPlanAsync(second, HourlyJob.JobId, 2));

        await second.InvokeAsync(new RequestJob(HourlyJob.JobId, due.Ticks + TimeSpan.TicksPerSecond));
        Assert.Equal(0L, await ScalarAsync(second.ConnectionString, $"""SELECT count(*) FROM "JobRun" WHERE "JobId" = '{HourlyJob.JobId}'"""));
        await second.InvokeAsync(new RequestJob(HourlyJob.JobId, due.Ticks));
        Assert.Equal("schedule|true", await ScalarAsync(second.ConnectionString, $"""
            SELECT r."Trigger" || '|' || (j."NextDueAt" > now() + interval '59 minutes') FROM "JobRun" r JOIN "Job" j ON j."Id" = r."JobId"
            WHERE r."JobId" = '{HourlyJob.JobId}'
            """));
    }

    [PostgreSqlFact]
    public async Task A5_a_fixed_plan_runs_at_its_occurrences()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_every_", services => services.AddJob<TickJob>(), durable: true);
        var clock = Stopwatch.StartNew();
        while ((long)(await ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) FROM "JobRun" WHERE "JobId" = '{TickJob.JobId}' AND "Trigger" = 'schedule' AND "State" = 'succeeded'
            """))! < 2)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "The plan did not run twice.");
            await Task.Delay(200);
        }
        output.WriteLine($"A5 two occurrences of Every(2 s) in {clock.Elapsed.TotalSeconds:0.0} s (Wolverine polls scheduled messages every 5 s)");
    }

    [PostgreSqlFact]
    public async Task A5_a_data_plan_reads_the_data_its_caller_committed_never_fires_before_it_and_a_time_in_the_past_runs_at_once()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_data_", services => services.AddJob<DataJob>(), durable: true);
        await ExecuteAsync(host.ConnectionString, "CREATE TABLE data_due (at timestamptz NOT NULL, done boolean NOT NULL DEFAULT false)");
        var second = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddHours(1);

        // The caller writes its queue row and reschedules in one transaction: NextDueAsync must see the row. A fraction rounds up.
        await host.InvokeAsync(new PlanProbeJob(DataJob.JobId, second.AddMilliseconds(250)));
        Assert.Equal(second.AddSeconds(1), await WaitForPlanAsync(host, DataJob.JobId, 1));

        await host.InvokeAsync(new PlanProbeJob(DataJob.JobId, DateTime.UtcNow.AddMinutes(-1)));
        var runId = await WaitForRunAsync(host, DataJob.JobId);
        await WaitForAsync(host, runId, JobStates.Succeeded);
        Assert.Equal(JobTriggers.Schedule, await ScalarAsync(host.ConnectionString, $"""SELECT "Trigger" FROM "JobRun" WHERE "Id" = {runId}"""));
        var clock = Stopwatch.StartNew();
        while (await ScalarAsync(host.ConnectionString, $"""SELECT "NextDueAt" FROM "Job" WHERE "Id" = '{DataJob.JobId}'""") is not DBNull)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "The run did not plan again from its data.");
            await Task.Delay(50);
        }
    }

    /// <summary>Wolverine runs with AutoCreate.None, so a package bump that wants another schema needs a migration: a guard, so
    /// tests/changed.ps1 runs it with every server change, a project file's included.</summary>
    [Trait("Category", "Guard")]
    [PostgreSqlFact]
    public async Task A6_the_migrations_together_are_the_schema_wolverine_expects()
    {
        // A7: warm starts (the first host of the process compiles every handler), alternating, the schema clone included.
        await (await AtlasTestHost.CreateAsync("atlas_jobs_a7_warm_")).DisposeAsync();
        var starts = new Dictionary<bool, List<long>> { [false] = [], [true] = [] };
        for (var i = 0; i < 6; i++)
        {
            var clock = Stopwatch.StartNew();
            await using (await AtlasTestHost.CreateAsync("atlas_jobs_a7_", durable: i % 2 == 1))
            {
                starts[i % 2 == 1].Add(clock.ElapsedMilliseconds);
            }
        }
        output.WriteLine($"A7 host start, ms: without durability {string.Join(", ", starts[false])}; with {string.Join(", ", starts[true])}");
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_a6_", durable: true);

        var database = (Weasel.Core.Migrations.IDatabase)host.Services.GetRequiredService<IWolverineRuntime>().Storage;
        var migration = await database.CreateMigrationAsync();
        Assert.True(migration.Difference == SchemaPatchDifference.None, string.Join(", ", migration.Deltas
            .Where(delta => delta.Difference != SchemaPatchDifference.None).Select(delta => $"{delta.SchemaObject.Identifier}: {delta.Difference}")));
    }

    [PostgreSqlFact]
    public async Task A8_runs_leave_no_wolverine_line_from_information_up_and_a_failure_stays_in_its_run()
    {
        var logs = new CaptureLoggerProvider();
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_a8_",
            services => services.AddJob<StepJob>().AddJob<FatalJob>().AddLogging(logging => logging.AddProvider(logs)), durable: true);
        await Task.Delay(TimeSpan.FromSeconds(1));
        logs.Entries.Clear();

        for (var i = 0; i < 3; i++)
        {
            await WaitForAsync(host, (await host.InvokeAsync<long?>(new RequestProbeJob(StepJob.JobId)))!.Value, JobStates.Succeeded);
        }
        var failed = (await host.InvokeAsync<long?>(new RequestProbeJob(FatalJob.JobId)))!.Value;
        await WaitForAsync(host, failed, JobStates.Failed);

        var loud = logs.Entries.Where(entry => entry.Category.StartsWith("Wolverine", StringComparison.Ordinal) && entry.Level >= LogLevel.Information)
            .Select(entry => $"{entry.Level} {entry.Category}: {entry.Message}").ToArray();
        Assert.True(loud.Length == 0, string.Join("\n", loud));
        Assert.StartsWith("Atlas.Server.Platform.Jobs.JobFailedException: Nejde to", (string)(await ScalarAsync(host.ConnectionString,
            $"""SELECT "Error" FROM "JobRun" WHERE "Id" = {failed}"""))!);
    }

    /// <summary>The fail-fast start (plan §4.4) on the first 3.3 start: the server migrates a 3.2.2 database, migration 2026110401
    /// included, before Wolverine's Solo start touches its tables, so an upgrade does not loop on restarts.</summary>
    [PostgreSqlFact]
    public async Task A_durable_server_upgrades_a_3_2_2_database_before_wolverine_starts()
    {
        await using var site = await AtlasHttpSite.CreateEmptyAsync("atlas_jobs_upgrade_start_", settings => settings["Jobs:Durable"] = "true");
        await ApplyBeforeAsync(site.ConnectionString, 2026110400);
        Assert.Equal(true, await ScalarAsync(site.ConnectionString, "SELECT to_regclass('wolverine_nodes') IS NULL"));

        var runtime = site.Services.GetRequiredService<IWolverineRuntime>();

        Assert.IsNotType<Wolverine.Persistence.Durability.NullMessageStore>(runtime.Storage);
        Assert.Equal(9L, await ScalarAsync(site.ConnectionString, """
            SELECT count(*) FROM pg_tables WHERE schemaname = current_schema() AND tablename LIKE 'wolverine\_%'
            """));
    }

    /// <summary>With the database down a durable server does not start: one line says why, and the restart policy starts it again.</summary>
    [PostgreSqlFact]
    public async Task A_durable_server_with_the_database_down_refuses_to_start_and_says_why()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_jobs_down_", settings =>
        {
            settings["Jobs:Durable"] = "true";
            settings["ConnectionStrings:atlas-db"] = "Host=127.0.0.1;Port=1;Database=atlas;Username=atlas;Password=atlas;Timeout=3";
        });

        Assert.ThrowsAny<Exception>(() => site.Services);

        var line = Assert.Single(site.Logs.Entries, entry => entry.Category == typeof(DurableStartGate).FullName);
        Assert.Equal(LogLevel.Critical, line.Level);
        Assert.Contains("restart policy", line.Message);
    }

    [Fact]
    public void A_server_is_durable_with_a_database_and_without_one_runs_the_setup_wizard_as_a_mediator()
    {
        static bool Durable(params (string Key, string Value)[] settings) => JobQueue.IsDurable(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => KeyValuePair.Create(setting.Key, (string?)setting.Value))).Build());

        Assert.False(Durable());
        Assert.True(Durable(("ConnectionStrings:atlas-db", "Host=db")));
        Assert.False(Durable(("ConnectionStrings:atlas-db", "Host=db"), ("Jobs:Durable", "false")));
    }

    private static async Task<DateTime> WaitForPlanAsync(AtlasTestHost host, string jobId, long envelopes)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (await ScalarAsync(host.ConnectionString, $"""SELECT "NextDueAt" FROM "Job" WHERE "Id" = '{jobId}'""") is DateTime due
                && (long)(await ScalarAsync(host.ConnectionString,
                    "SELECT count(*) FROM wolverine_incoming_envelopes WHERE message_type LIKE '%RequestJob' AND status = 'Scheduled'"))! >= envelopes)
            {
                return due.ToUniversalTime();
            }
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"{jobId} has no plan.");
            await Task.Delay(50);
        }
    }

    private static async Task WaitForAsync(AtlasTestHost host, long runId, string state)
    {
        var clock = Stopwatch.StartNew();
        string? current;
        while ((current = (string?)await ScalarAsync(host.ConnectionString, $"""SELECT "State" FROM "JobRun" WHERE "Id" = {runId}""")) != state)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"The run {runId} is {current}, not {state}.");
            await Task.Delay(50);
        }
    }

    private static async Task<long> WaitForRunAsync(AtlasTestHost host, string jobId)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (await ScalarAsync(host.ConnectionString, $"""SELECT max("Id") FROM "JobRun" WHERE "JobId" = '{jobId}'""") is long id)
            {
                return id;
            }
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"No run of {jobId}.");
            await Task.Delay(50);
        }
    }
}

/// <summary>Sleeps outside any transaction, as a remote call between steps.</summary>
public sealed class SleepJob(string id) : IJobDefinition
{
    public string Id => id;
    public string Title => "Spí";
    public string Description => "Čeká 1,5 s.";

    public Task RunAsync(JobContext context, CancellationToken ct) => Task.Delay(TimeSpan.FromSeconds(1.5), ct);
}

/// <summary>Three steps, each recorded once in gate_steps; its first attempt waits after the first step until the host stops.</summary>
public sealed class GateJob : IJobDefinition
{
    public const string JobId = "test.gate";
    public string Id => JobId;
    public string Title => "Brána";
    public string Description => "Tři kroky přes restart.";

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var done = context.Progress<StepJob.StepProgress>()?.Done ?? 0;
        while (done < 3)
        {
            var next = done + 1;
            done = (await context.StepAsync(async (session, token) =>
            {
                await session.CreateSQLQuery("INSERT INTO gate_steps (step) VALUES (:step)").SetParameter("step", next).ExecuteUpdateAsync(token);
                return new StepJob.StepProgress(next);
            }, ct)).Done;
            if (context.Attempt == 1)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        }
    }
}

public sealed class HourlyJob : IJobDefinition
{
    public const string JobId = "test.hourly";
    public string Id => JobId;
    public string Title => "Každou hodinu";
    public string Description => "Pevný plán.";
    public JobSchedule? Schedule => JobSchedule.Every(TimeSpan.FromHours(1));

    public Task RunAsync(JobContext context, CancellationToken ct) => Task.CompletedTask;
}

public sealed class TickJob : IJobDefinition
{
    public const string JobId = "test.tick";
    public string Id => JobId;
    public string Title => "Každé 2 s";
    public string Description => "Krátký pevný plán.";
    public JobSchedule? Schedule => JobSchedule.Every(TimeSpan.FromSeconds(2));

    public Task RunAsync(JobContext context, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Due at the earliest open row of data_due, read in its own session; a run closes them all.</summary>
public sealed class DataJob : IJobDefinition
{
    public const string JobId = "test.data";
    public string Id => JobId;
    public string Title => "Podle dat";
    public string Description => "Plán z dat.";
    public JobSchedule? Schedule => JobSchedule.FromData;

    public async Task<DateTime?> NextDueAsync(IServiceProvider services, CancellationToken ct)
    {
        using var session = services.GetRequiredService<AtlasDatabase>().OpenSession();
        return await session.CreateSQLQuery("SELECT min(at) FROM data_due WHERE NOT done").UniqueResultAsync<DateTime?>(ct);
    }

    public Task RunAsync(JobContext context, CancellationToken ct)
        => context.StepAsync(async (session, token) => await session.CreateSQLQuery("UPDATE data_due SET done = true").ExecuteUpdateAsync(token), ct);
}

/// <summary>A feature's handler: its queue row and the reschedule in one transaction.</summary>
public sealed record PlanProbeJob(string JobId, DateTime? At);

public static class PlanProbeJobHandler
{
    public static async Task Handle(PlanProbeJob message, ISession session, IJobRequests jobs, CancellationToken ct)
    {
        if (message.At is { } at)
        {
            await session.CreateSQLQuery("INSERT INTO data_due (at) VALUES (:at)").SetParameter("at", at).ExecuteUpdateAsync(ct);
        }
        await jobs.RescheduleAsync(message.JobId, ct);
    }
}
