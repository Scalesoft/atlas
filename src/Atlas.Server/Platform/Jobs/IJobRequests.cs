namespace Atlas.Server.Platform.Jobs;

/// <summary>Asks for a run (§4.2). Call it from a handler: it writes in the handler's transaction (the scoped ISession), so a
/// rollback leaves nothing and the commit starts the run at once.</summary>
public interface IJobRequests
{
    /// <summary>Queues a run of <paramref name="jobId"/> unless one is already queued for the same key (requests merge: a hundred
    /// saves give at most one more run). A paused job keeps the queued run until it is resumed.</summary>
    /// <returns>The queued run's id, or null when the request merged into one already queued.</returns>
    Task<long?> RequestAsync(string jobId, string reason, string? key = null, DateTime? notBefore = null, CancellationToken ct = default);

    /// <summary>After the feature's own data changed (backup time, a queue item's NextAttemptAt): asks a <see cref="JobSchedule.FromData"/>
    /// job again for its next due time; an occurrence planned before no longer runs.</summary>
    Task RescheduleAsync(string jobId, CancellationToken ct = default);
}

/// <summary>What an administrator does with jobs (§4.6, §4.7). The caller checks the rights; every method writes in the caller's
/// transaction, as <see cref="IJobRequests"/>.</summary>
public interface IJobControl
{
    /// <summary>„Spustit teď“: runs even a paused job (the explicit will of the administrator). Merges into a queued run.</summary>
    Task<long> RunNowAsync(string jobId, Guid? accountId, string reason, CancellationToken ct = default);

    /// <summary>„Zkusit znovu“: the failed or cancelled run again, from its Progress; returns its id.</summary>
    Task<long> RetryAsync(long runId, Guid? accountId, CancellationToken ct = default);

    /// <summary>„Zastavit“: a queued run ends cancelled at once, a running one at its next step or heartbeat; what committed stays.</summary>
    Task CancelAsync(long runId, Guid? accountId, CancellationToken ct = default);

    /// <summary>The one pause of every trigger. Resuming starts the run its requests queued meanwhile.</summary>
    Task SetPausedAsync(string jobId, bool paused, Guid? accountId, CancellationToken ct = default);
}

/// <summary>Runs a job to its end in the calling flow, outside the queue: for tests and tools that need the result before going on.</summary>
public interface IJobRunner
{
    /// <returns>The run's id; read its JobRun for the state, summary and log.</returns>
    Task<long> RunToEndAsync(string jobId, string reason, string? key = null, CancellationToken ct = default);
}
