using Atlas.Server.Features.Jobs;
using Atlas.Server.Features.Jobs.Queries;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Hub;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Atlas.Server.Tests.Jobs;

public sealed class SummaryRecurringJob : IJobDefinition
{
    public string Id => "test.recurring";
    public string Title => "Zkušební pravidelná úloha";
    public string Description => "Pravidelná úloha, jejíž termín minul.";
    public JobSchedule? Schedule => JobSchedule.Every(TimeSpan.FromMinutes(30));
    public Task RunAsync(JobContext context, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>The top of Úlohy na pozadí (3.6): the counts of ListJobs over existing runs, as an effective wiki administrator (a Hub one) and a
/// space administrator see them, and a statement count that does not grow with the runs. The host has no scheduler (outbox), so nothing runs
/// or plans by itself and the seeded rows stay as they are.</summary>
public sealed class JobSummaryTests(ITestOutputHelper output)
{
    [PostgreSqlFact]
    public async Task The_summary_counts_the_visible_runs_in_sql_and_flags_overdue_and_stuck_jobs()
    {
        // Private: the summary counts every run of the installation (JobRun), and the Hub administrator comes with an enforced Hub connection.
        await using var host = await AtlasTestHost.CreateAsync("atlas_jobs_summary_", services =>
        {
            services.AddJob<TestPlatformJob>();
            services.AddJob<TestSpaceJob>();
            services.AddJob<SummaryRecurringJob>();
        }, outbox: true);
        var spaceAdmin = await host.SeedUserAsync("spravce");
        var own = await host.SeedSpaceAsync("Vlastní prostor", (spaceAdmin, SpaceRole.Admin));
        var foreign = await host.SeedSpaceAsync("Cizí prostor");
        await host.SeedAsync(session => session.CreateSQLQuery("""
            INSERT INTO "Job" ("Id","ModuleId","NextDueAt") VALUES ('test.platform','platform',NULL),('test.space','platform',NULL),
              ('test.recurring','platform',now() - interval '1 hour') ON CONFLICT ("Id") DO UPDATE SET "NextDueAt" = EXCLUDED."NextDueAt";
            INSERT INTO "JobRun" ("JobId","Trigger","State","Attempt","SpaceId","QueuedAt","StartedAt","FinishedAt","Error") VALUES
              -- Three successes of 60, 100 and 120 s (median 100 s), then a run going for 20 min: more than 10 min, so stuck.
              ('test.platform','schedule','succeeded',1,NULL,now() - interval '3 days',now() - interval '3 days',now() - interval '3 days' + interval '60 seconds',NULL),
              ('test.platform','schedule','succeeded',1,NULL,now() - interval '2 days',now() - interval '2 days',now() - interval '2 days' + interval '100 seconds',NULL),
              ('test.platform','schedule','succeeded',1,NULL,now() - interval '1 day',now() - interval '1 day',now() - interval '1 day' + interval '120 seconds',NULL),
              ('test.platform','schedule','running',1,NULL,now() - interval '20 minutes',now() - interval '20 minutes',NULL,NULL),
              ('test.recurring','schedule','succeeded',1,NULL,now() - interval '2 hours',now() - interval '2 hours',now() - interval '2 hours',NULL),
              ('test.space','event','failed',1,:own,now() - interval '2 hours',now() - interval '2 hours',now() - interval '2 hours',E'System.InvalidOperationException: nic\n   at Own()'),
              ('test.space','event','failed',1,:foreign,now() - interval '3 days',now() - interval '3 days',now() - interval '3 days','cizí'),
              ('test.space','event','failed',1,:own,now() - interval '20 days',now() - interval '20 days',now() - interval '20 days','staré'),
              ('test.space','event','queued',0,:own,now(),NULL,NULL,NULL);
            """).SetParameter("own", own).SetParameter("foreign", foreign).ExecuteUpdateAsync());

        // A Hub administrator is an effective wiki administrator (3.5.2 #7): every run.
        var (hubAdmin, _, _) = await HubEffectiveAuthorityTests.SeedAsync(host, true);
        Assert.False(hubAdmin.IsWikiAdmin);
        host.Actor.Current = hubAdmin;
        var list = await host.InvokeAsync<JobList>(new ListJobs());
        var summary = list.Summary;
        Assert.Equal((1, 1, 1, 2), (summary.Running, summary.Queued, summary.FailedDay, summary.FailedWeek));
        Assert.Equal(14, summary.Days.Count);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), summary.Days[^1].Day);
        Assert.Equal((4, 2), (summary.Days.Sum(day => day.Succeeded), summary.Days.Sum(day => day.Failed)));
        Assert.Equal(new[] { "System.InvalidOperationException: nic", "cizí" }, summary.Failures.Select(failure => failure.Error));
        Assert.All(summary.Failures, failure => Assert.Equal("Zkušební úloha prostoru", failure.JobTitle));

        var jobs = list.Jobs.ToDictionary(job => job.Id);
        Assert.Equal((true, false, true), (jobs["test.platform"].Recurring, jobs["test.space"].Recurring, jobs["test.recurring"].Recurring));
        Assert.Equal(100, jobs["test.platform"].TypicalSeconds);
        Assert.NotNull(jobs["test.platform"].LastSuccessAt);
        Assert.Null(jobs["test.space"].LastSuccessAt);
        Assert.Equal((true, false), (jobs["test.platform"].Stuck, jobs["test.platform"].Overdue));
        Assert.Equal((false, true), (jobs["test.recurring"].Stuck, jobs["test.recurring"].Overdue));
        Assert.Equal((false, false), (jobs["test.space"].Stuck, jobs["test.space"].Overdue));

        // A space administrator counts only the runs of their spaces.
        host.Actor.Current = spaceAdmin;
        var mine = (await host.InvokeAsync<JobList>(new ListJobs())).Summary;
        Assert.Equal((0, 1, 1, 1), (mine.Running, mine.Queued, mine.FailedDay, mine.FailedWeek));
        Assert.Equal(new[] { "System.InvalidOperationException: nic" }, mine.Failures.Select(failure => failure.Error));

        // The same statements however many runs there are: the counts are SQL's, not a loaded list's.
        host.Actor.Current = hubAdmin;
        async Task<IReadOnlyList<string>> StatementsAsync()
        {
            using var recording = SqlRecording.Start();
            await host.InvokeAsync<JobList>(new ListJobs());
            return recording.Statements;
        }
        var few = await StatementsAsync();
        await host.SeedAsync(session => session.CreateSQLQuery("""
            INSERT INTO "JobRun" ("JobId","Trigger","State","Attempt","QueuedAt","StartedAt","FinishedAt")
            SELECT 'test.recurring','schedule','succeeded',1,now() - n * interval '1 hour',now() - n * interval '1 hour',now() - n * interval '1 hour' + interval '5 seconds'
            FROM generate_series(3, 300) n
            """).ExecuteUpdateAsync());
        var many = await StatementsAsync();
        output.WriteLine($"{many.Count} statements:\n{string.Join("\n", many)}");
        Assert.Equal(few, many);
        Assert.True(many.Count <= 16, $"ListJobs sent {many.Count} statements");
    }
}
