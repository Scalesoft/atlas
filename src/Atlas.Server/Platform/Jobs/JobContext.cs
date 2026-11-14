using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Jobs;

/// <summary>One attempt of a run, handed to <see cref="IJobDefinition.RunAsync"/> (§4.4, §4.5). Implemented by the runner.</summary>
public abstract class JobContext
{
    public abstract long RunId { get; }

    public abstract string JobId { get; }

    /// <summary>One of <see cref="JobTriggers"/>.</summary>
    public abstract string Trigger { get; }

    /// <summary>Short Czech text: „Publikace stránky Provoz“, „Spustil Jan Novák“.</summary>
    public abstract string? Reason { get; }

    /// <summary>The key a keyed request merged on (one dashboard of a job that refreshes dashboards); null otherwise.</summary>
    public abstract string? Key { get; }

    /// <summary>The account that asked (a manual run); null for the system.</summary>
    public abstract Guid? RequestedBy { get; }

    /// <summary>1 for the first attempt.</summary>
    public abstract int Attempt { get; }

    /// <summary>Writes to the ordinary log under the job's category; entries from Information up also go to JobRun.Log (500 kept).
    /// Log counts and ids, never page text: the reader of the run log may not see the content.</summary>
    public abstract ILogger Log { get; }

    /// <summary>The run's scope: its ISession has no open transaction, an IMessageBus invoke is one transaction as in a handler.</summary>
    public abstract IServiceProvider Services { get; }

    /// <summary>The last committed checkpoint (JobRun.Progress), default when the run starts fresh.</summary>
    public abstract T? Progress<T>();

    /// <summary>The numbers for the UI (JobRun.Summary, „propojeno 120 stránek“); stored with the next step and at the end.</summary>
    public abstract void Summarize(object summary);

    /// <summary>One short transaction in its own scope: the step's work and the returned value as the new
    /// <see cref="Progress{T}"/> commit together.</summary>
    public abstract Task<T> StepAsync<T>(Func<ISession, CancellationToken, Task<T>> step, CancellationToken ct);
}

public static class JobTriggers
{
    public const string Startup = "startup";
    public const string Event = "event";
    public const string Schedule = "schedule";
    public const string Manual = "manual";
    public const string Retry = "retry";
}

public static class JobStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
