using System.Diagnostics;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using static Atlas.Server.Tests.Events.OutboxTests;

namespace Atlas.Server.Tests.Jobs;

/// <summary>The jobs runtime (3.3 plan §4.2–4.5): requests merge and roll back with their handler, a committed request runs at once on the
/// durable queue, attempts retry and fail, a stop request and a pause hold, a run left running continues from its Progress, and
/// <see cref="IJobRunner.RunToEndAsync"/> runs a job in the calling flow of a host without durability. Every host that registers a job is
/// the test's own: a job's queued and running runs (JobRun) and its pause (Job) are rows of the whole installation, which another test's
/// request of the same job would merge into or hold, and the durable ones start the queue and the scheduler over those tables.</summary>
public sealed class JobRuntimeTests
{
    [PostgreSqlFact]
    public async Task Requests_merge_into_one_queued_run_and_RunToEndAsync_runs_it_from_step_to_step()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_merge_", services => services.AddJob<StepJob>());

        var first = await host.InvokeAsync<long?>(new RequestProbeJob(StepJob.JobId));
        var merged = await host.InvokeAsync<long?>(new RequestProbeJob(StepJob.JobId));
        var runId = await host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(StepJob.JobId, "Test");

        Assert.NotNull(first);
        Assert.Null(merged);
        Assert.Equal(first, runId);
        Assert.Equal("succeeded|1|3|3|true", await ScalarAsync(host.ConnectionString, $"""
            SELECT "State" || '|' || "Attempt" || '|' || ("Progress"->>'done') || '|' || ("Summary"->>'steps') || '|' || ("FinishedAt" IS NOT NULL)
            FROM "JobRun" WHERE "Id" = {runId}
            """));
        Assert.Equal("krok 1,krok 2,krok 3", await ScalarAsync(host.ConnectionString, $"""
            SELECT string_agg(e->>'message', ',') FROM "JobRun", jsonb_array_elements("Log") e WHERE "Id" = {runId} AND e->>'level' = 'Information'
            """));
    }

    [PostgreSqlFact]
    public async Task A_rollback_leaves_neither_the_run_nor_its_envelope_and_a_commit_starts_the_run_at_once()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_commit_", services => services.AddJob<StepJob>(), durable: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.InvokeAsync<long?>(new RequestProbeJob(StepJob.JobId, Throw: true)));
        Assert.Equal(0L, await ScalarAsync(host.ConnectionString, $"""SELECT count(*) FROM "JobRun" WHERE "JobId" = '{StepJob.JobId}'"""));
        // The host's start sends the runs of its own startup jobs meanwhile; only one of the test's job would be the rollback's.
        Assert.Equal(0L, await ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) FROM wolverine_incoming_envelopes WHERE message_type LIKE '%RunJob' AND position('{StepJob.JobId}'::bytea IN body) > 0
            """));

        var runId = (await host.InvokeAsync<long?>(new RequestProbeJob(StepJob.JobId)))!.Value;
        await WaitForAsync(host, runId, JobStates.Succeeded);
        // A1: no polling between the commit and the start (the scheduled-message poll alone is 5 s).
        var wait = (double)(await ScalarAsync(host.ConnectionString, $"""
            SELECT extract(epoch FROM "StartedAt" - "QueuedAt")::float8 FROM "JobRun" WHERE "Id" = {runId}
            """))!;
        Assert.True(wait < 1, $"The run started {wait:0.000} s after its request.");
    }

    [PostgreSqlFact]
    public async Task Attempts_retry_until_the_last_and_a_failure_without_retry_ends_the_run_at_once()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_fail_", services => services.AddJob<FailingJob>().AddJob<FatalJob>());
        var runner = host.Services.GetRequiredService<IJobRunner>();

        var failing = await runner.RunToEndAsync(FailingJob.JobId, "Test");
        var fatal = await runner.RunToEndAsync(FatalJob.JobId, "Test");

        Assert.Equal("failed|3|true|true", await ScalarAsync(host.ConnectionString, $"""
            SELECT "State" || '|' || "Attempt" || '|' || ("Error" LIKE 'System.InvalidOperationException: Selhání%') || '|' || ("FinishedAt" IS NOT NULL)
            FROM "JobRun" WHERE "Id" = {failing}
            """));
        Assert.Equal("failed|1", await ScalarAsync(host.ConnectionString, $"""SELECT "State" || '|' || "Attempt" FROM "JobRun" WHERE "Id" = {fatal}"""));
        Assert.Equal(3L, await ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) FROM "JobRun", jsonb_array_elements("Log") e WHERE "Id" = {failing} AND e->>'level' = 'Error' AND e->>'message' = 'Selhání'
            """));
    }

    [PostgreSqlFact]
    public async Task A_failure_is_stored_without_the_credentials_its_exception_carries()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_redact_", services => services.AddJob<LeakingJob>());

        var runId = await host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(LeakingJob.JobId, "Test");

        var stored = (string)(await ScalarAsync(host.ConnectionString, $"""SELECT "Error" || '|' || "Log"::text FROM "JobRun" WHERE "Id" = {runId}"""))!;
        Assert.DoesNotContain("s3cret", stored);
        Assert.DoesNotContain("eyJhbGciOi", stored);
        Assert.DoesNotContain("hunter2", stored);
        Assert.Contains("Password=***;", stored);
        Assert.Contains("Bearer ***", stored);
        Assert.Contains("https://backup:***@git.example/atlas http://localhost:5432/atlas", stored);
    }

    [PostgreSqlFact]
    public async Task A_failed_attempt_waits_its_delay_on_the_durable_queue_and_the_last_one_fails_the_run()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_retry_", services => services.AddJob<FailingJob>(), durable: true);

        var runId = (await host.InvokeAsync<long?>(new RequestProbeJob(FailingJob.JobId)))!.Value;
        await WaitForAsync(host, runId, JobStates.Failed, TimeSpan.FromSeconds(40));

        // Delays of 2 s and 4 s, each at least 80 % with the jitter.
        Assert.Equal("3|true", await ScalarAsync(host.ConnectionString, $"""
            SELECT "Attempt" || '|' || ("FinishedAt" - "StartedAt" >= interval '4.8 seconds') FROM "JobRun" WHERE "Id" = {runId}
            """));
    }

    [PostgreSqlFact]
    public async Task A_stop_request_cancels_a_running_run_at_its_next_step()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_cancel_", services => services.AddJob<LoopJob>());

        var running = host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(LoopJob.JobId, "Test");
        var runId = await WaitForRunAsync(host, LoopJob.JobId, JobStates.Running);
        await host.InvokeAsync(new CancelProbeRun(runId));

        Assert.Equal(runId, await running.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal("cancelled|true|true", await ScalarAsync(host.ConnectionString, $"""
            SELECT "State" || '|' || "CancelRequested" || '|' || ("FinishedAt" IS NOT NULL) FROM "JobRun" WHERE "Id" = {runId}
            """));
    }

    [PostgreSqlFact]
    public async Task A_paused_job_keeps_its_request_queued_and_resuming_runs_it()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_pause_", services => services.AddJob<StepJob>(), durable: true);

        await host.InvokeAsync(new PauseProbeJob(StepJob.JobId, true));
        var runId = (await host.InvokeAsync<long?>(new RequestProbeJob(StepJob.JobId)))!.Value;
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(JobStates.Queued, await ScalarAsync(host.ConnectionString, $"""SELECT "State" FROM "JobRun" WHERE "Id" = {runId}"""));

        await host.InvokeAsync(new PauseProbeJob(StepJob.JobId, false));
        await WaitForAsync(host, runId, JobStates.Succeeded);
    }

    [PostgreSqlFact]
    public async Task A_run_left_running_by_the_last_process_continues_from_its_progress_and_counts_the_attempt()
    {
        var schema = await DisposableSchema.CreateMigratedAsync("atlas_jobs_restart_");
        await ExecuteAsync(schema.ConnectionString, $$"""
            INSERT INTO "JobRun" ("JobId", "Trigger", "State", "Attempt", "StartedAt", "HeartbeatAt", "Progress")
            VALUES ('{{StepJob.JobId}}', 'event', 'running', 1, now() - interval '1 hour', now() - interval '1 hour', '{"done": 1}');
            """);

        await using var host = await AtlasTestHost.CreateAsync(schema, services => services.AddJob<StepJob>(), durable: true);
        // This test's run, not a startup run of another job the host queued beside it.
        var runId = (long)(await ScalarAsync(host.ConnectionString, $"""SELECT "Id" FROM "JobRun" WHERE "JobId" = '{StepJob.JobId}'"""))!;
        await WaitForAsync(host, runId, JobStates.Succeeded);

        Assert.Equal("2|3", await ScalarAsync(host.ConnectionString, $"""
            SELECT "Attempt" || '|' || ("Progress"->>'done') FROM "JobRun" WHERE "Id" = {runId}
            """));
        Assert.Equal("Server se restartoval během běhu, běh pokračuje od posledního kroku.,krok 2,krok 3", await ScalarAsync(host.ConnectionString, $"""
            SELECT string_agg(e->>'message', ',') FROM "JobRun", jsonb_array_elements("Log") e WHERE "Id" = {runId}
            """));
    }

    [PostgreSqlFact]
    public async Task Migration_2026110401_creates_the_wolverine_tables_beside_existing_jobs()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_jobs_wolverine_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110401);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "Job" ("Id", "ModuleId") VALUES ('test.steps', 'platform');
            INSERT INTO "JobRun" ("JobId", "Trigger", "State") VALUES ('test.steps', 'event', 'queued');
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("test.steps|queued", await ScalarAsync(schema.ConnectionString, """
            SELECT j."Id" || '|' || r."State" FROM "Job" j JOIN "JobRun" r ON r."JobId" = j."Id"
            """));
        Assert.Equal(9L, await ScalarAsync(schema.ConnectionString, """
            SELECT count(*) FROM pg_tables WHERE schemaname = current_schema() AND tablename IN ('wolverine_incoming_envelopes',
              'wolverine_outgoing_envelopes', 'wolverine_dead_letters', 'wolverine_dead_letter_history', 'wolverine_nodes',
              'wolverine_node_assignments', 'wolverine_control_queue', 'wolverine_node_records', 'wolverine_agent_restrictions')
            """));
    }

    /// <summary>Migration 2026110400 (the jobs contract) over an installation with accounts: the tables come empty, and the indexes merge
    /// queued requests of a job and key and allow one running run of a job.</summary>
    [PostgreSqlFact]
    public async Task Migration_2026110400_adds_the_job_tables_whose_indexes_merge_requests_and_hold_one_running_run()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_jobs_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110400);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
              VALUES ('00000000-0000-0000-0000-000000000001','admin','Admin',true,true,'stamp');
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("admin|0|0", await ScalarAsync(schema.ConnectionString, """
            SELECT u."Username" || '|' || (SELECT count(*) FROM "Job") || '|' || (SELECT count(*) FROM "JobRun") FROM "UserAccount" u
            """));
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "JobRun" ("JobId", "Key", "Trigger", "State") VALUES ('a', NULL, 'event', 'queued'), ('a', 'k', 'event', 'queued'),
              ('a', NULL, 'event', 'running');
            """);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(schema.ConnectionString,
            """INSERT INTO "JobRun" ("JobId", "Trigger", "State") VALUES ('a', 'event', 'queued')"""));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(schema.ConnectionString,
            """INSERT INTO "JobRun" ("JobId", "Trigger", "State") VALUES ('a', 'event', 'running')"""));
    }

    [PostgreSqlFact]
    public async Task Requests_and_plans_of_a_job_this_host_does_not_register_do_nothing()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_unknown_");

        Assert.Null(await host.InvokeAsync<long?>(new RequestProbeJob("test.missing")));
        await host.InvokeAsync(new PlanProbeJob("test.missing", null));

        // The shared host: other tests' jobs run beside it, the missing job has no row.
        Assert.Equal(0L, await ScalarAsync(host.ConnectionString, """
            SELECT (SELECT count(*) FROM "JobRun" WHERE "JobId" = 'test.missing') + (SELECT count(*) FROM "Job" WHERE "Id" = 'test.missing')
            """));
    }

    [PostgreSqlFact]
    public async Task A_host_without_durability_never_runs_a_request_in_the_background()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_idle_", services => services.AddJob<StepJob>());

        var runId = (await host.InvokeAsync<long?>(new RequestProbeJob(StepJob.JobId)))!.Value;
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        Assert.Equal("queued|0", await ScalarAsync(host.ConnectionString, $"""SELECT "State" || '|' || "Attempt" FROM "JobRun" WHERE "Id" = {runId}"""));
    }

    [PostgreSqlFact]
    public async Task A_failed_write_of_a_runs_end_is_tried_again_and_the_next_run_of_the_job_starts()
    {
        var schema = await DisposableSchema.CreateMigratedAsync("atlas_jobs_blip_");
        // The first write of the job's end fails, as a database blip would (the host's startup jobs end too).
        await ExecuteAsync(schema.ConnectionString, $$"""
            CREATE SEQUENCE end_blip;
            CREATE FUNCTION end_blip() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."State" = 'succeeded' AND NEW."JobId" = '{{StepJob.JobId}}' THEN
                    IF nextval('end_blip') = 1 THEN RAISE EXCEPTION 'blip'; END IF;
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER end_blip BEFORE UPDATE ON "JobRun" FOR EACH ROW EXECUTE FUNCTION end_blip();
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, services => services.AddJob<StepJob>(), durable: true);

        var first = (await host.InvokeAsync<long?>(new RequestProbeJob(StepJob.JobId)))!.Value;
        await WaitForAsync(host, first, JobStates.Succeeded);
        var second = (await host.InvokeAsync<long?>(new RequestProbeJob(StepJob.JobId)))!.Value;
        await WaitForAsync(host, second, JobStates.Succeeded);

        Assert.Equal("1|true", await ScalarAsync(host.ConnectionString, $"""
            SELECT "Attempt" || '|' || EXISTS (SELECT 1 FROM jsonb_array_elements("Log") e WHERE e->>'message' LIKE 'Výsledek pokusu se nepodařilo uložit%')
            FROM "JobRun" WHERE "Id" = {first}
            """));
    }

    [PostgreSqlFact]
    public async Task A_request_merged_into_a_queued_run_holds_it_until_the_requester_commits_and_a_run_by_hand_takes_it_over()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_hold_", services => services.AddJob<StepJob>());
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var queued = (await host.InvokeAsync<long?>(new RequestProbeJob(StepJob.JobId)))!.Value;

        // The run's begin locks it FOR UPDATE, so it waits for the requester and reads what the requester wrote with the request.
        await host.InvokeAsync(new HoldProbeJob(StepJob.JobId, async () => Assert.Equal(PostgresErrorCodes.LockNotAvailable,
            (await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(host.ConnectionString,
                $"""SELECT 1 FROM "JobRun" WHERE "Id" = {queued} FOR UPDATE NOWAIT"""))).SqlState)));

        Assert.Equal(queued, await host.InvokeAsync<long>(new RunNowProbeJob(StepJob.JobId, admin.Id)));
        Assert.Equal($"queued|manual|Ručně|{admin.Id}", await ScalarAsync(host.ConnectionString, $"""
            SELECT "State" || '|' || "Trigger" || '|' || "Reason" || '|' || "RequestedBy" FROM "JobRun" WHERE "JobId" = '{StepJob.JobId}'
            """));
    }

    [PostgreSqlFact]
    public async Task A_pause_holds_a_run_requested_before_it_until_the_resume()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_held_", services => services.AddJob<StepJob>(), durable: true);

        // Its RunJob is sent before the pause, in the same transaction, and arrives after the commit.
        var runId = (await host.InvokeAsync<long?>(new RequestProbeJob(StepJob.JobId, ThenPause: true)))!.Value;
        var clock = Stopwatch.StartNew();
        while ((long)(await ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) FROM wolverine_incoming_envelopes
            WHERE message_type LIKE '%RunJob' AND position('{StepJob.JobId}'::bytea IN body) > 0 AND status <> 'Handled'
            """))! > 0)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "The RunJob sent before the pause was not handled.");
            await Task.Delay(50);
        }
        Assert.Equal("queued|0", await ScalarAsync(host.ConnectionString, $"""SELECT "State" || '|' || "Attempt" FROM "JobRun" WHERE "Id" = {runId}"""));

        await host.InvokeAsync(new PauseProbeJob(StepJob.JobId, false));
        await WaitForAsync(host, runId, JobStates.Succeeded);
    }

    [PostgreSqlFact]
    public async Task A_heavy_job_waits_queued_while_pages_are_edited_until_its_deadline()
    {
        // The start counts as an edit and the window is an hour: only the deadline lets the run start.
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_quiet_", services => services.AddJob<QuietJob>()
            .Replace(ServiceDescriptor.Singleton(new JobQuietOptions { Window = TimeSpan.FromHours(1), Postpone = TimeSpan.FromSeconds(1), Deadline = TimeSpan.FromSeconds(3) })),
            durable: true);

        var runId = (await host.InvokeAsync<long?>(new RequestProbeJob(QuietJob.JobId)))!.Value;
        // The runner postpones it (NotBefore, kept until it starts) when it gets to the run: wait for that, not for a fixed time a loaded
        // host may overrun before the runner looked at all.
        var clock = Stopwatch.StartNew();
        string? seen;
        while ((seen = (string?)await ScalarAsync(host.ConnectionString,
                   $"""SELECT "State" || '|' || "Attempt" || '|' || ("NotBefore" IS NOT NULL) FROM "JobRun" WHERE "Id" = {runId}""")) == "queued|0|false")
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"The run {runId} was never postponed.");
            await Task.Delay(50);
        }
        Assert.Equal("queued|0|true", seen);
        await WaitForAsync(host, runId, JobStates.Succeeded);
        Assert.True((bool)(await ScalarAsync(host.ConnectionString,
            $"""SELECT "StartedAt" >= "QueuedAt" + interval '3 seconds' FROM "JobRun" WHERE "Id" = {runId}"""))!);
    }

    [PostgreSqlFact]
    public async Task A_paused_job_starts_a_run_by_hand_queued_behind_another_when_that_one_ends_but_keeps_an_event_run_queued()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_hand_", services => services.AddJob<LoopJob>(), durable: true);
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        await host.InvokeAsync(new PauseProbeJob(LoopJob.JobId, true));

        var first = await host.InvokeAsync<long>(new RunNowProbeJob(LoopJob.JobId, admin.Id));
        await WaitForAsync(host, first, JobStates.Running);
        var second = await host.InvokeAsync<long>(new RunNowProbeJob(LoopJob.JobId, admin.Id));
        var automatic = (await host.InvokeAsync<long?>(new RequestProbeJob(LoopJob.JobId, Key: "event")))!.Value;
        await host.InvokeAsync(new CancelProbeRun(first));

        await WaitForAsync(host, second, JobStates.Running);
        await host.InvokeAsync(new CancelProbeRun(second));
        await WaitForAsync(host, second, JobStates.Cancelled);
        Assert.Equal(JobStates.Queued, await ScalarAsync(host.ConnectionString, $"""SELECT "State" FROM "JobRun" WHERE "Id" = {automatic}"""));
    }

    [PostgreSqlFact]
    public async Task The_start_sends_a_queued_run_by_hand_of_a_paused_job_but_not_its_event_run()
    {
        var schema = await DisposableSchema.CreateMigratedAsync("atlas_jobs_paused_start_");
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "Job" ("Id", "ModuleId", "Paused") VALUES ('{StepJob.JobId}', 'test', true);
            INSERT INTO "JobRun" ("JobId", "Key", "Trigger", "State") VALUES ('{StepJob.JobId}', NULL, 'manual', 'queued'), ('{StepJob.JobId}', 'event', 'event', 'queued');
            """);

        await using var host = await AtlasTestHost.CreateAsync(schema, services => services.AddJob<StepJob>(), durable: true);
        var manual = (long)(await ScalarAsync(host.ConnectionString, $"""SELECT "Id" FROM "JobRun" WHERE "JobId" = '{StepJob.JobId}' AND "Trigger" = 'manual'"""))!;
        await WaitForAsync(host, manual, JobStates.Succeeded);

        Assert.Equal("queued|0", await ScalarAsync(host.ConnectionString, $"""
            SELECT "State" || '|' || "Attempt" FROM "JobRun" WHERE "JobId" = '{StepJob.JobId}' AND "Trigger" = 'event'
            """));
    }

    private static async Task WaitForAsync(AtlasTestHost host, long runId, string state, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        string? current;
        while ((current = (string?)await ScalarAsync(host.ConnectionString, $"""SELECT "State" FROM "JobRun" WHERE "Id" = {runId}""")) != state)
        {
            Assert.True(clock.Elapsed < (timeout ?? TimeSpan.FromSeconds(20)), $"The run {runId} is {current}, not {state}.");
            await Task.Delay(50);
        }
    }

    private static async Task<long> WaitForRunAsync(AtlasTestHost host, string jobId, string state)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (await ScalarAsync(host.ConnectionString, $"""SELECT "Id" FROM "JobRun" WHERE "JobId" = '{jobId}' AND "State" = '{state}'""") is long id)
            {
                return id;
            }
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"No run of {jobId} is {state}.");
            await Task.Delay(50);
        }
    }
}

/// <summary>Three steps, each its own transaction; it starts from the committed Progress.</summary>
public sealed class QuietJob : IJobDefinition
{
    public const string JobId = "test.quiet";
    public string Id => JobId;
    public string Title => "Klid";
    public string Description => "Těžká úloha, která čeká na klid.";
    public bool RunsWhenQuiet => true;

    public Task RunAsync(JobContext context, CancellationToken ct) => Task.CompletedTask;
}

public sealed class StepJob : IJobDefinition
{
    public const string JobId = "test.steps";
    public string Id => JobId;
    public string Title => "Kroky";
    public string Description => "Tři kroky s průběhem.";

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var done = context.Progress<StepProgress>()?.Done ?? 0;
        while (done < 3)
        {
            var next = done + 1;
            done = (await context.StepAsync(async (session, token) =>
            {
                await session.CreateSQLQuery("SELECT 1").UniqueResultAsync<int>(token);
                return new StepProgress(next);
            }, ct)).Done;
            context.Log.LogInformation("krok {Step}", done);
        }
        context.Summarize(new { steps = done });
    }

    public sealed record StepProgress(int Done);
}

public sealed class FailingJob : IJobDefinition
{
    public const string JobId = "test.failing";
    public string Id => JobId;
    public string Title => "Selhává";
    public string Description => "Každý pokus selže.";
    // Above BeginAsync's 1 s margin: a second RunJob of the run (the start sends queued runs again) cannot begin an attempt before its delay.
    public JobRetry Retry => new(3, TimeSpan.FromSeconds(2));

    public Task RunAsync(JobContext context, CancellationToken ct) => throw new InvalidOperationException("Selhání");
}

public sealed class FatalJob : IJobDefinition
{
    public const string JobId = "test.fatal";
    public string Id => JobId;
    public string Title => "Selže hned";
    public string Description => "Selže bez dalších pokusů.";

    public Task RunAsync(JobContext context, CancellationToken ct) => throw new JobFailedException("Nejde to", retry: false);
}

public sealed class LeakingJob : IJobDefinition
{
    public const string JobId = "test.leaking";
    public string Id => JobId;
    public string Title => "Prozradí heslo";
    public string Description => "Selže s heslem a tokenem ve zprávě.";

    public Task RunAsync(JobContext context, CancellationToken ct) => throw new JobFailedException(
        "Host=db;Username=atlas;Password=s3cret;Database=atlas Authorization: Bearer eyJhbGciOi.payload " +
        "https://backup:hunter2@git.example/atlas http://localhost:5432/atlas", retry: false);
}

/// <summary>Steps until it is stopped.</summary>
public sealed class LoopJob : IJobDefinition
{
    public const string JobId = "test.loop";
    public string Id => JobId;
    public string Title => "Smyčka";
    public string Description => "Kroky do zastavení.";

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        while (true)
        {
            await context.StepAsync((_, _) => Task.FromResult(1), ct);
            await Task.Delay(50, ct);
        }
    }
}

public sealed record RequestProbeJob(string JobId, bool Throw = false, string? Key = null, bool ThenPause = false);

/// <summary>A request, then <paramref name="WhileOpen"/> before the handler's transaction commits.</summary>
public sealed record HoldProbeJob(string JobId, Func<Task> WhileOpen);

public sealed record RunNowProbeJob(string JobId, Guid By);

public sealed record PauseProbeJob(string JobId, bool Paused);

public sealed record CancelProbeRun(long RunId);

/// <summary>A handler's requests and controls, in its transaction.</summary>
public static class JobProbeHandler
{
    public static async Task<long?> Handle(RequestProbeJob message, IJobRequests jobs, IJobControl control, CancellationToken ct)
    {
        var id = await jobs.RequestAsync(message.JobId, "Test", message.Key, ct: ct);
        if (message.ThenPause)
        {
            await control.SetPausedAsync(message.JobId, true, null, ct);
        }
        return message.Throw ? throw new InvalidOperationException("Rollback") : id;
    }

    public static async Task Handle(HoldProbeJob message, IJobRequests jobs, CancellationToken ct)
    {
        await jobs.RequestAsync(message.JobId, "Test", ct: ct);
        await message.WhileOpen();
    }

    public static Task<long> Handle(RunNowProbeJob message, IJobControl jobs, CancellationToken ct) => jobs.RunNowAsync(message.JobId, message.By, "Ručně", ct);

    public static Task Handle(PauseProbeJob message, IJobControl jobs, CancellationToken ct) => jobs.SetPausedAsync(message.JobId, message.Paused, null, ct);

    public static Task Handle(CancelProbeRun message, IJobControl jobs, CancellationToken ct) => jobs.CancelAsync(message.RunId, null, ct);
}
