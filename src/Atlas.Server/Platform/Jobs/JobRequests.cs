using Atlas.Server.Platform.Events;
using Cronos;
using NHibernate;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Jobs;

/// <summary>Requests and administration of runs in the caller's transaction (3.3 plan §4.2, §4.4): the rows by SQL, <see cref="RunJob"/>
/// and <see cref="RequestJob"/> published in the same transaction (NHibernateEnvelopeTransaction), so a rollback leaves nothing.</summary>
public sealed class JobRequests(ISession session, IMessageContext messages, IEnumerable<IJobDefinition> definitions, IServiceProvider services,
    ILogger<JobRequests> logger) : IJobRequests, IJobControl
{
    private const string Queued = """
        INSERT INTO "JobRun" ("JobId", "Key", "Trigger", "Reason", "RequestedBy", "State", "NotBefore")
        VALUES (:job, :key, :trigger, :reason, :by, 'queued', :notBefore)
        ON CONFLICT ("JobId", (COALESCE("Key", ''))) WHERE "State" = 'queued'
        """;

    public async Task<long?> RequestAsync(string jobId, string reason, string? key = null, DateTime? notBefore = null, CancellationToken ct = default)
    {
        if (Find(jobId) is null)
        {
            // A host without the job's module or its registration (test hosts leave definitions out): its handlers still request it.
            logger.LogDebug("No job {JobId} is registered; the request is dropped.", jobId);
            return null;
        }
        return await QueueAsync(jobId, JobTriggers.Event, reason, key, notBefore, ct);
    }

    public async Task RescheduleAsync(string jobId, CancellationToken ct = default)
    {
        if (Find(jobId) is not { Schedule: JobSchedule.DataSchedule })
        {
            logger.LogDebug("No job {JobId} planned by its data is registered; nothing to plan.", jobId);
            return;
        }
        // NextDueAsync reads the feature's data in its own scope, so it runs after this transaction commits what the caller wrote.
        await SendAsync(new PlanJob(jobId), null);
    }

    public async Task<long> RunNowAsync(string jobId, Guid? accountId, string reason, CancellationToken ct = default)
    {
        _ = Find(jobId) ?? throw new ApiException(404, "Úloha neexistuje.");
        var id = await UpsertQueuedAsync(jobId, JobTriggers.Manual, reason, null, accountId, ct);
        // Even a paused job: the explicit will of the administrator.
        await PublishRunAsync(id, jobId, null);
        return id;
    }

    public async Task<long> RetryAsync(long runId, Guid? accountId, CancellationToken ct = default)
    {
        // Fresh attempts from the run's Progress; a run of the same key already queued takes the retry instead.
        var jobId = await session.CreateSQLQuery("""
            UPDATE "JobRun" r SET "State" = 'queued', "Trigger" = 'retry', "RequestedBy" = :by, "Attempt" = 0, "CancelRequested" = false,
                "FinishedAt" = NULL, "NotBefore" = NULL, "QueuedAt" = now()
            WHERE r."Id" = :id AND r."State" IN ('failed', 'cancelled') AND NOT EXISTS (SELECT 1 FROM "JobRun" q
                WHERE q."JobId" = r."JobId" AND COALESCE(q."Key", '') = COALESCE(r."Key", '') AND q."State" = 'queued')
            RETURNING r."JobId"
            """).AddScalar("JobId", NHibernateUtil.String).SetParameter("id", runId).SetParameter("by", accountId, NHibernateUtil.Guid)
            .UniqueResultAsync<string?>(ct);
        if (jobId is not null)
        {
            await PublishRunAsync(runId, jobId, null);
            return runId;
        }
        var queued = await session.CreateSQLQuery("""
            SELECT q."Id", q."JobId" FROM "JobRun" r JOIN "JobRun" q ON q."JobId" = r."JobId" AND COALESCE(q."Key", '') = COALESCE(r."Key", '')
            WHERE r."Id" = :id AND r."State" IN ('failed', 'cancelled') AND q."State" = 'queued'
            """).AddScalar("Id", NHibernateUtil.Int64).AddScalar("JobId", NHibernateUtil.String).SetParameter("id", runId)
            .UniqueResultAsync<object[]?>(ct) ?? throw new ApiException(409, "Zkusit znovu jde jen běh, který selhal nebo byl zastaven.");
        await PublishRunAsync((long)queued[0], (string)queued[1], null);
        return (long)queued[0];
    }

    public async Task CancelAsync(long runId, Guid? accountId, CancellationToken ct = default)
    {
        var job = await session.QueryOver<JobRun>().Where(run => run.Id == runId).Select(run => run.JobId).SingleOrDefaultAsync<string?>(ct);
        if (job is null)
        {
            return;
        }
        // Under the job's lock, as a run's end (JobRunner): a run requested meanwhile either waits for this end or is sent by it.
        await JobRunner.LockAsync(services, job, ct);
        // Queued, or waiting for its next attempt: it ends at once. Running: its step or heartbeat reads the request.
        var ended = await session.CreateSQLQuery("""
            UPDATE "JobRun" SET "CancelRequested" = true,
                "State" = CASE WHEN "State" = 'queued' OR "NotBefore" IS NOT NULL THEN 'cancelled' ELSE "State" END,
                "FinishedAt" = CASE WHEN "State" = 'queued' OR "NotBefore" IS NOT NULL THEN now() ELSE "FinishedAt" END,
                "NotBefore" = NULL
            WHERE "Id" = :id AND "State" IN ('queued', 'running')
            RETURNING "JobId", "State"
            """).AddScalar("JobId", NHibernateUtil.String).AddScalar("State", NHibernateUtil.String).SetParameter("id", runId)
            .UniqueResultAsync<object[]?>(ct);
        if (ended is [string jobId, JobStates.Cancelled])
        {
            await EndedAsync(jobId, ct);
        }
    }

    public async Task SetPausedAsync(string jobId, bool paused, Guid? accountId, CancellationToken ct = default)
    {
        var job = (Find(jobId) is { } definition ? await LockJobAsync(definition, ct) : await session.GetAsync<Job>(jobId, LockMode.Upgrade, ct))
            ?? throw new ApiException(404, "Úloha neexistuje.");
        if (job.Paused == paused)
        {
            return;
        }
        job.Paused = paused;
        job.PausedBy = paused ? accountId : null;
        job.PausedAt = paused ? DateTime.UtcNow : null;
        if (!paused)
        {
            await PublishQueuedAsync(jobId, ct);
        }
    }

    /// <summary>A queued run of the job, merged into one already queued; for <see cref="IJobRunner"/>, which runs it in the calling flow.</summary>
    internal Task<long> QueueToRunAsync(string jobId, string reason, string? key, CancellationToken ct)
        => UpsertQueuedAsync(jobId, JobTriggers.Manual, reason, key, null, ct);

    /// <summary>After a run ended: the queued runs of its job start (a second run waits for the first), and a FromData job plans again.</summary>
    internal async Task EndedAsync(string jobId, CancellationToken ct)
    {
        await PublishQueuedAsync(jobId, ct);
        await RescheduleAsync(jobId, ct);
    }

    /// <summary>The first success of a <see cref="JobSchedule.Once"/> job.</summary>
    internal async Task CompletedAsync(IJobDefinition definition, CancellationToken ct)
    {
        var job = await LockJobAsync(definition, ct);
        job.CompletedAt ??= DateTime.UtcNow;
    }

    /// <summary>A planned occurrence: a scheduled run and, for a fixed plan, the next occurrence. A FromData job plans again after the run.</summary>
    internal async Task OnScheduleAsync(RequestJob message, CancellationToken ct)
    {
        if (Find(message.JobId) is not { } definition)
        {
            return;
        }
        var job = await LockJobAsync(definition, ct);
        if (job.NextDueAt?.Ticks != message.Revision)
        {
            return;
        }
        await QueueAsync(definition.Id, JobTriggers.Schedule, "Plánovaný běh", null, null, ct);
        job.NextDueAt = null;
        await PlanAsync(job, Next(definition.Schedule));
    }

    /// <summary>A FromData job's next due time, from its committed data (<see cref="RescheduleAsync"/>).</summary>
    internal async Task OnPlanAsync(PlanJob message, CancellationToken ct)
    {
        if (Find(message.JobId) is { Schedule: JobSchedule.DataSchedule } definition)
        {
            await PlanAsync(await LockJobAsync(definition, ct), Due(await definition.NextDueAsync(services, ct)));
        }
    }

    /// <summary>At the start (JobScheduler): the Job rows, the startup and one-time runs, the plans, and the runs requested by SQL or left
    /// by the last process. A plan is published again at every start: its revision keeps one occurrence valid, a lost one is replaced.</summary>
    internal async Task StartAsync(CancellationToken ct)
    {
        // Read first, so a startup run queued below is sent once, by QueueAsync.
        var paused = (await session.QueryOver<Job>().Where(job => job.Paused).Select(job => job.Id).ListAsync<string>(ct)).ToHashSet();
        var waiting = await session.QueryOver<JobRun>().WhereRestrictionOn(run => run.State).IsIn(new[] { JobStates.Running, JobStates.Queued })
            .ListAsync(ct);
        foreach (var definition in definitions)
        {
            var job = await LockJobAsync(definition, ct);
            switch (definition.Schedule)
            {
                case JobSchedule.StartupSchedule:
                case JobSchedule.OnceSchedule when job.CompletedAt is null:
                    await QueueAsync(definition.Id, JobTriggers.Startup, "Start serveru", null, null, ct);
                    break;
                case JobSchedule.EverySchedule or JobSchedule.CronSchedule or JobSchedule.DataSchedule:
                    // A fixed plan keeps its time, and one missed while the server was down runs at once, once.
                    job.NextDueAt = definition.Schedule is JobSchedule.DataSchedule
                        ? Due(await definition.NextDueAsync(services, ct))
                        : job.NextDueAt ?? Next(definition.Schedule);
                    if (job.NextDueAt is { } due)
                    {
                        await SendAsync(new RequestJob(job.Id, due.Ticks), due);
                    }
                    break;
            }
        }
        // JobRunner starts only what may start: a run still running in this process, or waiting for its time, ignores the message.
        foreach (var run in waiting.Where(run => run.State == JobStates.Running || !paused.Contains(run.JobId) || Asked(run)))
        {
            await PublishRunAsync(run.Id, run.JobId, run.NotBefore);
        }
    }

    private async Task<long?> QueueAsync(string jobId, string trigger, string reason, string? key, DateTime? notBefore, CancellationToken ct)
    {
        // FOR SHARE: a resume committing meanwhile either waits for this request and sends its run, or this request reads it. First, so a
        // plan holding the job (OnScheduleAsync) never waits for a request that waits for it.
        var paused = await session.CreateSQLQuery("""SELECT "Paused" FROM "Job" WHERE "Id" = :id FOR SHARE""").AddScalar("Paused", NHibernateUtil.Boolean)
            .SetParameter("id", jobId).UniqueResultAsync<bool?>(ct) == true;
        // A merge locks the queued run until the caller commits, so its begin waits and the run reads what the caller wrote with the request.
        var row = await Parameters(session.CreateSQLQuery(Queued + """ DO UPDATE SET "QueuedAt" = "JobRun"."QueuedAt" RETURNING "Id", xmax = 0 AS "Created" """),
                jobId, trigger, reason, key, null)
            .AddScalar("Created", NHibernateUtil.Boolean).SetParameter("notBefore", notBefore, NHibernateUtil.UtcDateTime).UniqueResultAsync<object[]>(ct);
        if (row[1] is not true)
        {
            return null;
        }
        var id = (long)row[0];
        if (!paused)
        {
            await PublishRunAsync(id, jobId, notBefore);
        }
        return id;
    }

    /// <summary>A run by hand: a queued run it merges into becomes the administrator's (it starts even while the job is paused); a request
    /// without an account (<see cref="QueueToRunAsync"/>) keeps the account the run was queued for, the job may act as it.</summary>
    private Task<long> UpsertQueuedAsync(string jobId, string trigger, string reason, string? key, Guid? by, CancellationToken ct)
        => Parameters(session.CreateSQLQuery(Queued + """
                    DO UPDATE SET "NotBefore" = NULL, "Trigger" = EXCLUDED."Trigger", "Reason" = EXCLUDED."Reason",
                        "RequestedBy" = COALESCE(EXCLUDED."RequestedBy", "JobRun"."RequestedBy")
                    RETURNING "Id"
                """), jobId, trigger, reason, key, by)
            .SetParameter("notBefore", null, NHibernateUtil.UtcDateTime).UniqueResultAsync<long>(ct);

    private static ISQLQuery Parameters(ISQLQuery query, string jobId, string trigger, string reason, string? key, Guid? by)
    {
        query.AddScalar("Id", NHibernateUtil.Int64).SetParameter("job", jobId).SetParameter("key", key, NHibernateUtil.String)
            .SetParameter("trigger", trigger).SetParameter("reason", reason).SetParameter("by", by, NHibernateUtil.Guid);
        return query;
    }

    private async Task PublishQueuedAsync(string jobId, CancellationToken ct)
    {
        var paused = (await session.GetAsync<Job>(jobId, ct))?.Paused == true;
        foreach (var run in await session.QueryOver<JobRun>().Where(queued => queued.JobId == jobId && queued.State == JobStates.Queued).ListAsync(ct))
        {
            if (!paused || Asked(run))
            {
                await PublishRunAsync(run.Id, run.JobId, run.NotBefore);
            }
        }
    }

    /// <summary>A run an administrator asked for: a pause holds only the automatic ones (JobRunner's begin).</summary>
    private static bool Asked(JobRun run) => run.Trigger is JobTriggers.Manual or JobTriggers.Retry;

    private Task PublishRunAsync(long runId, string jobId, DateTime? notBefore) => SendAsync(new RunJob(runId, jobId), notBefore);

    /// <summary>Through the caller's transaction, at once or at <paramref name="at"/>. Only a durable host routes job messages (JobQueue):
    /// in another one a request waits queued for <see cref="IJobRunner"/> and nothing runs in the background.</summary>
    private async Task SendAsync<T>(T message, DateTime? at) where T : class
    {
        if (messages is MessageContext { Storage: NullMessageStore })
        {
            return;
        }
        if (at is { } time && time > DateTime.UtcNow)
        {
            await messages.ScheduleAsync(message, time);
        }
        else
        {
            await messages.PublishAsync(message);
        }
    }

    /// <summary>A changed plan: the new occurrence becomes the valid one; an unchanged one keeps the occurrence already published.</summary>
    private async Task PlanAsync(Job job, DateTime? next)
    {
        if (job.NextDueAt == next)
        {
            return;
        }
        job.NextDueAt = next;
        if (next is { } at)
        {
            await SendAsync(new RequestJob(job.Id, at.Ticks), at);
        }
    }

    private async Task<Job> LockJobAsync(IJobDefinition definition, CancellationToken ct)
    {
        await session.CreateSQLQuery("""INSERT INTO "Job" ("Id", "ModuleId") VALUES (:id, :module) ON CONFLICT ("Id") DO NOTHING""")
            .SetParameter("id", definition.Id).SetParameter("module", Outbox.ModuleOf(definition.GetType())).ExecuteUpdateAsync(ct);
        return await session.GetAsync<Job>(definition.Id, LockMode.Upgrade, ct);
    }

    private IJobDefinition? Find(string jobId) => definitions.FirstOrDefault(definition => definition.Id == jobId);

    /// <summary>The next occurrence of a fixed plan, from now; null for the others.</summary>
    private static DateTime? Next(JobSchedule? schedule) => Second(schedule switch
    {
        JobSchedule.EverySchedule every => DateTime.UtcNow + every.Interval,
        JobSchedule.CronSchedule cron => CronExpression.Parse(cron.Expression).GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc),
        _ => null,
    });

    /// <summary>A data plan's time rounded up to whole seconds: its occurrence never fires before the data is due, which would run the job
    /// for nothing and plan the same past time again.</summary>
    private static DateTime? Due(DateTime? time) => Second(time?.AddTicks(TimeSpan.TicksPerSecond - 1));

    /// <summary>Whole seconds: the revision of a plan is its ticks, and timestamptz keeps microseconds only.</summary>
    private static DateTime? Second(DateTime? time)
        => time is { } t ? new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc) : null;
}
