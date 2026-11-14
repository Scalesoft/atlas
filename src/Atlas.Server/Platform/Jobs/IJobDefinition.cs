namespace Atlas.Server.Platform.Jobs;

/// <summary>A background job (3.3 plan §4.1): the definition is code, its state lives in <see cref="Job"/> and <see cref="JobRun"/>.
/// One per job, registered as a singleton by its module's AddServices (<see cref="JobServiceCollectionExtensions.AddJob{TJob}"/>), a
/// plugin's module the same way. The module (and so the right to see it) follows from the namespace, as Outbox.ModuleOf.</summary>
public interface IJobDefinition
{
    /// <summary>"backups.zip", "contacts.link", "retention"; unique and stable, it keys the rows.</summary>
    string Id { get; }

    /// <summary>Czech, for the list: "Záloha ZIP".</summary>
    string Title { get; }

    /// <summary>What it does and why, one Czech sentence.</summary>
    string Description { get; }

    JobRetry Retry => JobRetry.Default;

    /// <summary>Null: it runs only when requested (<see cref="IJobRequests"/>), by an event or by hand.</summary>
    JobSchedule? Schedule => null;

    /// <summary>A heavy job that yields to editing: its automatic runs start only when no page was edited for a while (<see cref="JobQuiet"/>);
    /// a run an administrator asks for starts at once.</summary>
    bool RunsWhenQuiet => false;

    /// <summary>For <see cref="JobSchedule.FromData"/>: when the next run is due by the feature's own data (the backup time, the earliest
    /// NextAttemptAt of a queue); null when nothing is due. Asked after every run and on <see cref="IJobRequests.RescheduleAsync"/>.</summary>
    Task<DateTime?> NextDueAsync(IServiceProvider services, CancellationToken ct) => Task.FromResult<DateTime?>(null);

    /// <summary>The body. It runs outside the message transaction: short transactions through <see cref="JobContext.StepAsync{T}"/>
    /// or Wolverine commands, remote calls between them. Throwing retries by <see cref="Retry"/>; <see cref="JobFailedException"/> with
    /// retry false fails the run at once. <paramref name="ct"/> is cancelled by a stop request and by shutdown.</summary>
    Task RunAsync(JobContext context, CancellationToken ct);
}

/// <summary>When a job runs by itself (§4.2). Any job can also be requested by an event or by hand.</summary>
public abstract record JobSchedule
{
    /// <summary>A fixed plan: each occurrence schedules the next one (§4.2); an occurrence missed while the server was down runs once at
    /// the next start.</summary>
    public sealed record EverySchedule(TimeSpan Interval) : JobSchedule;

    /// <summary>A fixed plan by a cron expression (Cronos, UTC).</summary>
    public sealed record CronSchedule(string Expression) : JobSchedule;

    /// <summary>On every start, once the database is initialized.</summary>
    public sealed record StartupSchedule : JobSchedule;

    /// <summary>On start until it succeeds once (Job.CompletedAt), e.g. a content migration.</summary>
    public sealed record OnceSchedule : JobSchedule;

    /// <summary>At the time <see cref="IJobDefinition.NextDueAsync"/> computes from the feature's data; a time in the past runs at once.</summary>
    public sealed record DataSchedule : JobSchedule;

    public static JobSchedule Every(TimeSpan interval) => new EverySchedule(interval);
    public static JobSchedule Cron(string expression) => new CronSchedule(expression);
    public static readonly JobSchedule AtStartup = new StartupSchedule();
    public static readonly JobSchedule Once = new OnceSchedule();
    public static readonly JobSchedule FromData = new DataSchedule();
}

/// <summary>Attempts of one run and the first delay; each next delay doubles, with jitter (§4.3).</summary>
public sealed record JobRetry(int Attempts, TimeSpan FirstDelay)
{
    /// <summary>5 attempts: 30 s, 1 min, 2 min, 4 min between them.</summary>
    public static readonly JobRetry Default = new(5, TimeSpan.FromSeconds(30));

    /// <summary>One attempt: a scheduled job whose next occurrence is the retry.</summary>
    public static readonly JobRetry None = new(1, TimeSpan.Zero);
}

/// <summary>Ends the attempt as failed; with <paramref name="retry"/> false the run fails at once, without the remaining attempts.</summary>
public sealed class JobFailedException(string message, bool retry = false, Exception? inner = null) : Exception(message, inner)
{
    public bool Retry { get; } = retry;
}

public static class JobServiceCollectionExtensions
{
    public static IServiceCollection AddJob<TJob>(this IServiceCollection services) where TJob : class, IJobDefinition
        => services.AddSingleton<IJobDefinition, TJob>();
}
