using Atlas.Server.Platform.Events.Queries;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Modules;
using NHibernate;
using NHibernate.Linq;
using Wolverine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Jobs.Queries;

public sealed record ListJobs;

/// <param name="Page">From 1; <see cref="JobQueryHandler.PageSize"/> runs each, newest first.</param>
public sealed record ListJobRuns(string JobId, int Page = 1);

public sealed record GetJobRun(string JobId, long RunId);

/// <param name="Jobs">Every registered job and every row whose definition is gone, minus the jobs of disabled modules; a space administrator gets
/// only the jobs with runs of their spaces.</param>
/// <param name="SystemServices">The infrastructure loops, for a wiki administrator only.</param>
/// <param name="Summary">What is running, waiting and failing, over the same runs.</param>
public sealed record JobList(IReadOnlyList<JobView> Jobs, IReadOnlyList<SystemServiceView> SystemServices, JobSummary Summary);

/// <param name="LastSuccessAt">When the newest run the viewer sees succeeded.</param>
public sealed record JobRunList(JobView Job, IReadOnlyList<JobRunView> Runs, int Total, int Page, int PageSize, DateTime? LastSuccessAt);

/// <summary>The jobs for Správa wiki → Provoz → Úlohy na pozadí, /api/v1/jobs and MCP (3.3 plan §4.6, §4.7). IServiceProvider, not the
/// registry itself: the registered definitions and the module catalog are whatever the host has, none in a host that registers no job.</summary>
public sealed class JobQueryHandler(ISession session, IContentAccess access, SpaceRoles roles, IServiceProvider services, SystemServiceHealth health,
    TimeProvider time, IMessageBus bus)
{
    public const int PageSize = 20;

    public async Task<JobList> Handle(ListJobs query, CancellationToken ct)
    {
        var viewer = await JobAccess.ViewerAsync(access, roles, ct);
        var rows = (await session.Query<Job>().ToListAsync(ct)).ToDictionary(row => row.Id, StringComparer.Ordinal);
        var definitions = services.GetServices<IJobDefinition>().ToDictionary(definition => definition.Id, StringComparer.Ordinal);
        var last = await JobLookup.LastRunsAsync(session, viewer, null, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var stats = await StatsAsync(viewer, ct);
        var jobs = definitions.Keys.Union(rows.Keys).Order(StringComparer.Ordinal)
            .Select(id => JobLookup.View(services, viewer, id, definitions.GetValueOrDefault(id), rows.GetValueOrDefault(id), last.GetValueOrDefault(id)))
            .OfType<JobView>().Select(job => WithStats(job, stats.GetValueOrDefault(job.Id), now)).ToList();
        return new JobList(jobs, viewer.WikiAdmin ? await SystemServicesAsync(ct) : [], await SummaryAsync(viewer, jobs, now, ct));
    }

    private static JobView WithStats(JobView job, (DateTime? LastSuccess, int? Typical) stats, DateTime now)
    {
        var going = job.LastRun?.State is JobStates.Queued or JobStates.Running;
        return job with
        {
            LastSuccessAt = stats.LastSuccess, TypicalSeconds = stats.Typical,
            Overdue = job.Recurring && !job.Paused && !going && job.NextDueAt < now - JobViews.OverdueAfter,
            Stuck = job.LastRun is { State: JobStates.Running, StartedAt: { } started } && now - started > JobViews.StuckAfter(stats.Typical)
        };
    }

    // Native SQL, category 5 of docs/architecture.md: FILTER counts and a median over the visible runs, one grouped statement each.
    // Every column of these statements has an alias: NHibernate reads a row by column name, and two unnamed count(*) would read as one.
    /// <summary>The runs the viewer may see, as SQL: <see cref="JobLookup.Visible"/>'s condition.</summary>
    private static string VisibleSql(JobViewer viewer) => viewer.WikiAdmin ? "" : """AND "SpaceId" IN (:spaces)""";

    private ISQLQuery Sql(JobViewer viewer, string sql)
    {
        var query = session.CreateSQLQuery(sql);
        return viewer.WikiAdmin ? query : (ISQLQuery)query.SetParameterList("spaces", viewer.Spaces);
    }

    /// <summary>Per job: when it last succeeded and how long a success takes (the median), one grouped statement.</summary>
    private async Task<Dictionary<string, (DateTime? LastSuccess, int? Typical)>> StatsAsync(JobViewer viewer, CancellationToken ct)
    {
        var rows = await Sql(viewer, $"""
            SELECT "JobId" AS job, max("FinishedAt") AS success, CAST(round(percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM "FinishedAt" - "StartedAt"))) AS int) AS typical
            FROM "JobRun" WHERE "State" = 'succeeded' {VisibleSql(viewer)} GROUP BY "JobId"
            """).ListAsync<object[]>(ct);
        return rows.ToDictionary(row => (string)row[0], row => ((DateTime?)row[1], (int?)row[2]), StringComparer.Ordinal);
    }

    private async Task<JobSummary> SummaryAsync(JobViewer viewer, IReadOnlyList<JobView> jobs, DateTime now, CancellationToken ct)
    {
        var (day, week, today) = (now.AddDays(-1), now.AddDays(-7), DateOnly.FromDateTime(now));
        var counts = (await Sql(viewer, $"""
            SELECT CAST(count(*) FILTER (WHERE "State" = 'running') AS int) AS running, CAST(count(*) FILTER (WHERE "State" = 'queued') AS int) AS queued,
              CAST(count(*) FILTER (WHERE "State" = 'failed' AND "FinishedAt" >= :day) AS int) AS failed_day, CAST(count(*) FILTER (WHERE "State" = 'failed') AS int) AS failed_week
            FROM "JobRun" WHERE ("State" IN ('running', 'queued') OR ("State" = 'failed' AND "FinishedAt" >= :week)) {VisibleSql(viewer)}
            """).SetParameter("day", day, NHibernateUtil.UtcDateTime).SetParameter("week", week, NHibernateUtil.UtcDateTime).ListAsync<object[]>(ct))[0];
        var perDay = (await Sql(viewer, $"""
            SELECT to_char("FinishedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD') AS day, CAST(count(*) FILTER (WHERE "State" = 'succeeded') AS int) AS succeeded,
              CAST(count(*) FILTER (WHERE "State" = 'failed') AS int) AS failed
            FROM "JobRun" WHERE "FinishedAt" >= :from {VisibleSql(viewer)} GROUP BY 1
            """).SetParameter("from", today.AddDays(-13).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), NHibernateUtil.UtcDateTime).ListAsync<object[]>(ct))
            .ToDictionary(row => DateOnly.ParseExact((string)row[0], "yyyy-MM-dd"), row => ((int)row[1], (int)row[2]));
        var days = Enumerable.Range(0, 14).Select(offset => today.AddDays(offset - 13))
            .Select(date => perDay.TryGetValue(date, out var count) ? new JobDayCount(date, count.Item1, count.Item2) : new JobDayCount(date, 0, 0)).ToList();
        // A failure of a job the list hides (a disabled module's) would link to a 404: only the listed jobs' failures are named.
        var titles = jobs.ToDictionary(job => job.Id, job => job.Title, StringComparer.Ordinal);
        var failed = await JobLookup.Visible(session, viewer).Where(run => run.State == JobStates.Failed && run.FinishedAt >= week)
            .OrderByDescending(run => run.FinishedAt).Take(JobViews.FailureCount).Select(run => new { run.Id, run.JobId, run.FinishedAt, run.Error }).ToListAsync(ct);
        var failures = failed.Where(run => titles.ContainsKey(run.JobId))
            .Select(run => new JobFailure(run.Id, run.JobId, titles[run.JobId], run.FinishedAt!.Value, run.Error is null ? null : JobViews.FirstLine(run.Error))).ToList();
        return new JobSummary((int)counts[0], (int)counts[1], (int)counts[2], (int)counts[3], days, failures);
    }

    public async Task<JobRunList> Handle(ListJobRuns query, CancellationToken ct)
    {
        var viewer = await JobAccess.ViewerAsync(access, roles, ct);
        var job = await JobLookup.FindAsync(session, services, viewer, query.JobId, ct);
        var runs = JobLookup.Visible(session, viewer).Where(run => run.JobId == query.JobId);
        var page = Math.Max(1, query.Page);
        var rows = await runs.OrderByDescending(run => run.Id).Skip((page - 1) * PageSize).Take(PageSize).Select(JobRunRow.Select).ToListAsync(ct);
        var success = await runs.Where(run => run.State == JobStates.Succeeded).Select(run => run.FinishedAt).OrderByDescending(at => at).FirstOrDefaultAsync(ct);
        return new JobRunList(job, rows.Select(row => row.ToView()).ToList(), await runs.CountAsync(ct), page, PageSize, success);
    }

    public async Task<JobRunDetail> Handle(GetJobRun query, CancellationToken ct)
    {
        var viewer = await JobAccess.ViewerAsync(access, roles, ct);
        var run = await JobLookup.RunAsync(session, services, viewer, query.JobId, query.RunId, ct);
        // The log holds counts and ids, but the stack of an exception names code a space administrator does not need.
        // The job's row is already in the session (RunAsync read it), so this is no second query.
        var paused = (await session.GetAsync<Job>(query.JobId, ct))?.Paused ?? false;
        return new JobRunDetail(JobViews.ToView(run), run.Error is null ? null : viewer.WikiAdmin ? run.Error : JobViews.FirstLine(run.Error), JobViews.LogOf(run), paused);
    }

    private async Task<IReadOnlyList<SystemServiceView>> SystemServicesAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var views = new List<SystemServiceView>();
        foreach (var info in SystemServices.All)
        {
            if (info.Id != SystemServices.Outbox)
            {
                views.Add(SystemServices.View(info, health.Last(info.Id), now));
                continue;
            }
            // Wolverine's queue beats nothing of its own: its state is the outbox's counts (an invoke runs in a scope and session of its own).
            var outbox = await bus.InvokeAsync<OutboxHealth>(new GetOutboxHealth(), ct);
            views.Add(SystemServices.View(info, null, now, $"Čeká {outbox.Pending}, mrtvé {outbox.Dead}",
                outbox.Dead > 0 ? outbox.LastError ?? "Mrtvé zprávy čekají na opakování." : null));
        }
        return views;
    }
}

/// <summary>What a viewer may see of a job: shared by the queries and the commands, so a job that is hidden is hidden for both.</summary>
internal static class JobLookup
{
    public const string Platform = "platform";

    /// <summary>The runs the viewer may see: all for a wiki administrator, those of their spaces for a space administrator.</summary>
    public static IQueryable<JobRun> Visible(ISession session, JobViewer viewer)
    {
        var runs = session.Query<JobRun>();
        var spaces = viewer.Spaces;
        return viewer.WikiAdmin ? runs : runs.Where(run => run.SpaceId != null && spaces.Contains(run.SpaceId.Value));
    }

    /// <summary>The newest visible run of each job (of <paramref name="jobId"/> only when given).</summary>
    public static async Task<Dictionary<string, JobRunView>> LastRunsAsync(ISession session, JobViewer viewer, string? jobId, CancellationToken ct)
    {
        var runs = Visible(session, viewer);
        if (jobId is not null)
        {
            runs = runs.Where(run => run.JobId == jobId);
        }
        var newest = await runs.GroupBy(run => run.JobId).Select(group => group.Max(run => run.Id)).ToListAsync(ct);
        if (newest.Count == 0)
        {
            return [];
        }
        var rows = await session.Query<JobRun>().Where(run => newest.Contains(run.Id)).Select(JobRunRow.Select).ToListAsync(ct);
        return rows.ToDictionary(row => row.JobId, row => row.ToView(), StringComparer.Ordinal);
    }

    /// <summary>The job as the viewer sees it, or null when it is hidden: the job of a disabled module, a job of no run of the viewer's spaces.</summary>
    public static JobView? View(IServiceProvider services, JobViewer viewer, string id, IJobDefinition? definition, Job? row, JobRunView? last)
    {
        var module = row?.ModuleId ?? Platform;
        if (services.GetService<ModuleCatalog>() is { } catalog && catalog.All.Any(item => item.Descriptor.Id == module) && !catalog.IsEnabled(module)
            || !viewer.WikiAdmin && last is null)
        {
            return null;
        }
        return new JobView(id, definition?.Title ?? "Neznámá úloha", definition?.Description ?? $"Úloha {id}, kterou tato verze Atlasu už nezná; zůstává jen její historie.",
            module, definition is null ? "—" : JobViews.ScheduleText(definition.Schedule), definition is null ? "—" : JobViews.RetryText(definition.Retry), definition is not null, row?.Paused ?? false, row?.NextDueAt, row?.CompletedAt, last,
            JobViews.IsRecurring(definition?.Schedule));
    }

    /// <summary>One job the viewer may see; 404 for one that does not exist or is hidden from them.</summary>
    public static async Task<JobView> FindAsync(ISession session, IServiceProvider services, JobViewer viewer, string jobId, CancellationToken ct)
    {
        var definition = services.GetServices<IJobDefinition>().FirstOrDefault(item => item.Id == jobId);
        var row = await session.GetAsync<Job>(jobId, ct);
        var last = (await LastRunsAsync(session, viewer, jobId, ct)).GetValueOrDefault(jobId);
        return (definition is null && row is null ? null : View(services, viewer, jobId, definition, row, last)) ?? throw NotFound("Úloha neexistuje.");
    }

    /// <summary>One run of a job, when the viewer may see it.</summary>
    public static async Task<JobRun> RunAsync(ISession session, IServiceProvider services, JobViewer viewer, string jobId, long runId, CancellationToken ct)
    {
        await FindAsync(session, services, viewer, jobId, ct);
        var run = await session.GetAsync<JobRun>(runId, ct);
        return run is null || run.JobId != jobId || !viewer.Sees(run) ? throw NotFound("Běh úlohy neexistuje.") : run;
    }

    private static ApiException NotFound(string message) => new(404, message, "not_found");
}
