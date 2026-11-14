using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Jobs;

/// <summary>A run without its checkpoint and log (the list and the history carry many).</summary>
/// <param name="Summary">The numbers the job reported for the UI (JobRun.Summary), as stored.</param>
public sealed record JobRunView(long Id, string JobId, string Trigger, string? Reason, string State, int Attempt, Guid? SpaceId, DateTime QueuedAt,
    DateTime? StartedAt, DateTime? HeartbeatAt, DateTime? FinishedAt, bool CancelRequested, JsonNode? Summary);

/// <param name="Module">The id of the module whose namespace holds the job (<c>platform</c> outside every module).</param>
/// <param name="Schedule">Czech text: when it runs by itself.</param>
/// <param name="Retry">Czech text: how often a failed run is tried again.</param>
/// <param name="Known">False for a row whose definition is gone („Neznámá úloha“): it keeps its history.</param>
/// <param name="CompletedAt">A job that runs once: when it first succeeded.</param>
/// <param name="LastRun">The newest run the viewer may see.</param>
/// <param name="Recurring">It runs by itself on a plan, at start or by the feature's data; false for a job run once, by an event or by hand.</param>
/// <param name="LastSuccessAt">The list only: when the newest visible run that succeeded finished.</param>
/// <param name="TypicalSeconds">The list only: the median duration of the visible runs that succeeded.</param>
/// <param name="Overdue">The list only: a recurring job, not paused, whose due time passed long ago and that has no run going.</param>
/// <param name="Stuck">The list only: its run has been going far longer than it typically takes (<see cref="JobViews.StuckAfter"/>).</param>
public sealed record JobView(string Id, string Title, string Description, string Module, string Schedule, string Retry, bool Known, bool Paused, DateTime? NextDueAt,
    DateTime? CompletedAt, JobRunView? LastRun, bool Recurring = false, DateTime? LastSuccessAt = null, int? TypicalSeconds = null, bool Overdue = false,
    bool Stuck = false);

/// <summary>The top of Úlohy na pozadí and of atlas_list_jobs, over the runs the viewer may see, counted in SQL (the runs age out after 30 days).</summary>
/// <param name="Days">The last 14 days (UTC), oldest first, every day present: the runs that finished then.</param>
/// <param name="Failures">The newest failed runs of the last 7 days, at most <see cref="JobViews.FailureCount"/>, with the first line of their error.</param>
public sealed record JobSummary(int Running, int Queued, int FailedDay, int FailedWeek, IReadOnlyList<JobDayCount> Days, IReadOnlyList<JobFailure> Failures);

public sealed record JobDayCount(DateOnly Day, int Succeeded, int Failed);

public sealed record JobFailure(long RunId, string JobId, string JobTitle, DateTime FinishedAt, string? Error);

public sealed record JobLogEntry(DateTime At, string Level, string Message);

/// <summary>The run a command started, retried or stopped (outside the Commands namespace, whose records are the commands).</summary>
public sealed record JobRunRef(long RunId);

/// <param name="Run">The run with its log; <c>Error</c> is the whole exception for a wiki administrator, its first line for a space administrator.</param>
/// <param name="Paused">The job is paused: a queued run of it waits for the resume (the page stops asking for it), unless an administrator started it.</param>
public sealed record JobRunDetail(JobRunView Run, string? Error, IReadOnlyList<JobLogEntry> Log, bool Paused);

/// <summary>The columns of a run the views need: a projection, so a list never reads the log and checkpoint of every run.</summary>
internal sealed class JobRunRow
{
    public long Id { get; init; }
    public string JobId { get; init; } = "";
    public string Trigger { get; init; } = "";
    public string? Reason { get; init; }
    public string State { get; init; } = "";
    public int Attempt { get; init; }
    public Guid? SpaceId { get; init; }
    public DateTime QueuedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? HeartbeatAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public bool CancelRequested { get; init; }
    public string? Summary { get; init; }

    public static readonly Expression<Func<JobRun, JobRunRow>> Select = run => new JobRunRow
    {
        Id = run.Id, JobId = run.JobId, Trigger = run.Trigger, Reason = run.Reason, State = run.State, Attempt = run.Attempt, SpaceId = run.SpaceId,
        QueuedAt = run.QueuedAt, StartedAt = run.StartedAt, HeartbeatAt = run.HeartbeatAt, FinishedAt = run.FinishedAt, CancelRequested = run.CancelRequested,
        Summary = run.Summary
    };

    public JobRunView ToView() => new(Id, JobId, Trigger, Reason, State, Attempt, SpaceId, QueuedAt, StartedAt, HeartbeatAt, FinishedAt, CancelRequested,
        Summary is null ? null : JsonNode.Parse(Summary));
}

internal static class JobViews
{
    public const int FailureCount = 20;

    /// <summary>A due time missed by more than this, with no run going, is „Po termínu“.</summary>
    public static readonly TimeSpan OverdueAfter = TimeSpan.FromMinutes(15);

    /// <summary>A run going three times its typical duration (at least 10 min), or an hour when nothing succeeded yet, is „Zaseknutá“.</summary>
    public static TimeSpan StuckAfter(int? typicalSeconds)
        => typicalSeconds is { } typical ? TimeSpan.FromSeconds(Math.Max(3 * typical, 600)) : TimeSpan.FromHours(1);

    public static bool IsRecurring(JobSchedule? schedule)
        => schedule is JobSchedule.EverySchedule or JobSchedule.CronSchedule or JobSchedule.DataSchedule or JobSchedule.StartupSchedule;

    public static JobRunView ToView(JobRun run) => JobRunRow.Select.Compile()(run).ToView();

    public static IReadOnlyList<JobLogEntry> LogOf(JobRun run)
        => run.Log is null ? [] : JsonSerializer.Deserialize<List<JobLogEntry>>(run.Log, JsonSerializerOptions.Web) ?? [];

    public static string FirstLine(string text) => text.Split('\n')[0].TrimEnd('\r');

    /// <summary>When the job runs by itself, in words for the list (§5).</summary>
    public static string ScheduleText(JobSchedule? schedule) => schedule switch
    {
        null => "Po události nebo ručně",
        JobSchedule.EverySchedule every => Every(every.Interval),
        JobSchedule.CronSchedule cron => $"Podle plánu {cron.Expression}",
        JobSchedule.StartupSchedule => "Při startu serveru",
        JobSchedule.OnceSchedule => "Jednou při startu, dokud neuspěje",
        JobSchedule.DataSchedule => "Podle nastavení funkce",
        _ => "Po události nebo ručně"
    };

    public static string RetryText(JobRetry retry) => retry.Attempts <= 1
        ? "Bez opakování"
        : $"Až {retry.Attempts} pokusů, první odklad {Duration(retry.FirstDelay)}, každý další dvojnásobný";

    public static string Duration(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => $"{Math.Max(1, (int)span.TotalSeconds)} s",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} min",
        _ => $"{(int)span.TotalHours} h"
    };

    public static string Every(TimeSpan interval) => interval switch
    {
        { TotalMinutes: < 1 } => $"Každých {Math.Max(1, (int)interval.TotalSeconds)} s",
        { TotalHours: < 1 } => (int)interval.TotalMinutes == 1 ? "Každou minutu" : $"Každých {(int)interval.TotalMinutes} min",
        { TotalDays: < 1 } => (int)interval.TotalHours == 1 ? "Každou hodinu" : $"Každých {(int)interval.TotalHours} h",
        _ => (int)interval.TotalDays == 1 ? "Každý den" : $"Každých {(int)interval.TotalDays} dní"
    };
}
