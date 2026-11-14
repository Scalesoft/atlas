using System.Text.Json;
using System.Text.RegularExpressions;
using Atlas.Server.Features.Notifications;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Live;
using NHibernate;
using NHibernate.Criterion;
using ILoggerFactory = Microsoft.Extensions.Logging.ILoggerFactory;
using Wolverine;
using Wolverine.Runtime;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Jobs;

/// <summary>Runs the attempts of job runs (3.3 plan §4.3–4.5): <see cref="RunJob"/> from the queue, and <see cref="RunToEndAsync"/> in the
/// calling flow. The database holds the truth: an attempt starts from a queued run, a due retry, or a run the last process left
/// running; a second run of the same job stays queued until the first ends (UX_JobRun_Running) and its end starts it. Each start, step,
/// retry wait and end is told after its commit to the jobs pages (<see cref="JobLiveChannel"/>), throttled per run.</summary>
public sealed class JobRunner(IServiceScopeFactory scopes, IEnumerable<IJobDefinition> definitions, ILoggerFactory loggers,
    IHostApplicationLifetime lifetime, LiveRelay relay, LiveThrottle throttle, JobQuiet quiet) : IJobRunner
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, IJobDefinition> m_definitions = definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);
    // A run marked running whose heartbeat is older than this process was left by a process that ended.
    private readonly DateTime m_started = DateTime.UtcNow;

    private enum Outcome { NotStarted, Retry, Ended }

    public async Task<long> RunToEndAsync(string jobId, string reason, string? key = null, CancellationToken ct = default)
    {
        if (!m_definitions.ContainsKey(jobId))
        {
            throw new InvalidOperationException($"The job {jobId} is not registered in this host.");
        }
        var runId = await InTransactionAsync(scopes, (services, _) => services.GetRequiredService<JobRequests>().QueueToRunAsync(jobId, reason, key, ct), ct);
        while (true)
        {
            switch (await AttemptAsync(runId, jobId, immediate: true, ct))
            {
                case Outcome.NotStarted:
                    throw new InvalidOperationException($"The run {runId} of the job {jobId} cannot start: another run of it is running.");
                case Outcome.Ended:
                    return runId;
            }
        }
    }

    internal Task HandleAsync(RunJob message) => AttemptAsync(message.RunId, message.JobId, immediate: false, lifetime.ApplicationStopping);

    /// <summary>A short transaction in its own scope, as a handler has it from <see cref="NHibernateTransactionMiddleware"/>: Wolverine
    /// messages published in it commit with it and go to their queue after the commit.</summary>
    internal static async Task<T> InTransactionAsync<T>(IServiceScopeFactory scopes, Func<IServiceProvider, ISession, Task<T>> work, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var messages = services.GetRequiredService<MessageContext>();
        var transaction = ActivatorUtilities.CreateInstance<NHibernateTransactionMiddleware>(services);
        await transaction.BeforeAsync(new Envelope(), messages);
        try
        {
            var result = await work(services, services.GetRequiredService<ISession>());
            await transaction.AfterAsync(ct);
            await messages.FlushOutgoingMessagesAsync();
            return result;
        }
        finally
        {
            transaction.Finally();
        }
    }

    /// <param name="immediate">The calling flow: no wait for the run's time or the next attempt's delay.</param>
    private async Task<Outcome> AttemptAsync(long runId, string jobId, bool immediate, CancellationToken ct)
    {
        // A disabled module's or a removed job's runs wait queued.
        if (!m_definitions.TryGetValue(jobId, out var definition))
        {
            return Outcome.NotStarted;
        }
        var (run, ended, restarted) = await InTransactionAsync(scopes, (services, session) => BeginAsync(services, session, definition, runId, immediate, ct), ct);
        if (ended is not null)
        {
            Announce(ended);
        }
        if (run is null)
        {
            return ended is not null ? Outcome.Ended : Outcome.NotStarted;
        }
        Announce(run);
        using var scope = scopes.CreateScope();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var context = new RunContext(scopes, run, scope.ServiceProvider, loggers.CreateLogger(definition.GetType()), cancel, Announce);
        // Every line the run logs, through any logger, carries the run: the stored log shows it at the run (AppLogProvider).
        using var logScope = context.Log.BeginScope(new Dictionary<string, object> { [JobLogScope.RunId] = run.Id, [JobLogScope.Name] = definition.Title });
        if (restarted)
        {
            context.Log.LogWarning("Server se restartoval během běhu, běh pokračuje od posledního kroku.");
        }
        Exception? failure = null;
        var state = JobStates.Succeeded;
        using (var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var beating = BeatAsync(context, heartbeat.Token);
            try
            {
                await definition.RunAsync(context, cancel.Token);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // A stop request: what committed stays.
                state = JobStates.Cancelled;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failure = ex;
            }
            finally
            {
                // Shutdown leaves the run running: the next start continues it from its Progress.
                await heartbeat.CancelAsync();
                await beating;
            }
        }
        var retry = failure is not null and not JobFailedException { Retry: false } && run.Attempt < definition.Retry.Attempts;
        if (failure is not null)
        {
            context.Log.LogError("{Error}", JobErrorText.Redact(failure.Message.Split('\n')[0]));
        }
        // Written even when a stop or the shutdown came meanwhile: the attempt is over. A failed write (a database blip) is tried again:
        // the run would stay running in this process and hold its job until a restart. The shutdown leaves it to the next start.
        for (var delay = TimeSpan.FromSeconds(1); ; delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30)))
        {
            try
            {
                JobRun? written = null;
                var outcome = await InTransactionAsync(scopes, async (services, session) =>
                {
                    // Under the job's lock, as BeginAsync: a run requested meanwhile either waits for this end or is sent by it.
                    await LockAsync(services, jobId, CancellationToken.None);
                    var current = written = await session.GetAsync<JobRun>(runId, LockMode.Upgrade);
                    context.Write(current, progress: null);
                    current.Error = failure is null ? null : JobErrorText.Redact(failure.ToString());
                    if (retry && !current.CancelRequested)
                    {
                        // Waits running, so a second run of the job does not start in between.
                        var wait = definition.Retry.FirstDelay * Math.Pow(2, run.Attempt - 1) * (0.8 + 0.4 * Random.Shared.NextDouble());
                        current.NotBefore = DateTime.UtcNow + wait;
                        if (!immediate)
                        {
                            await services.GetRequiredService<IMessageContext>().ScheduleAsync(new RunJob(runId, jobId), current.NotBefore.Value);
                        }
                        return Outcome.Retry;
                    }
                    await EndAsync(services, session, current, definition,
                        failure is null ? state : current.CancelRequested ? JobStates.Cancelled : JobStates.Failed, CancellationToken.None);
                    return Outcome.Ended;
                }, CancellationToken.None);
                Announce(written!);
                return outcome;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                context.Log.LogWarning("Výsledek pokusu se nepodařilo uložit ({ErrorType}), zkusí se to znovu.", ex.GetType().Name);
            }
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>Tells the jobs pages after a commit, at most every <see cref="LiveThrottle.Interval"/> per run, the run's state as committed.</summary>
    private void Announce(JobRun run)
    {
        var message = new JobRunChanged(run.Id, run.JobId, run.State, run.SpaceId);
        throttle.Post("job-run:" + run.Id, () => JobLiveChannel.PublishAsync(relay, message));
    }

    /// <summary>The job's lock: one begin or end of its runs at a time, so the check for another running run and the look for waiting
    /// runs hold.</summary>
    internal static Task LockAsync(IServiceProvider services, string jobId, CancellationToken ct)
        => services.GetRequiredService<IAtlasLocks>().AcquireAdvisoryAsync("atlas.job:" + jobId, ct);

    /// <returns>The run that starts now, or null and the run when it ended here (a stop request, a crash in its last attempt).</returns>
    private async Task<(JobRun? Run, JobRun? Ended, bool Restarted)> BeginAsync(IServiceProvider services, ISession session,
        IJobDefinition definition, long runId, bool immediate, CancellationToken ct)
    {
        await LockAsync(services, definition.Id, ct);
        var run = await session.GetAsync<JobRun>(runId, LockMode.Upgrade, ct);
        if (run is null || run.JobId != definition.Id || run.State is not (JobStates.Queued or JobStates.Running))
        {
            return (null, null, false);
        }
        if (run.CancelRequested)
        {
            await EndAsync(services, session, run, definition, JobStates.Cancelled, ct);
            return (null, run, false);
        }
        // A paused job starts only what an administrator asked for; a request sent or delayed before the pause waits for the resume.
        if (run is { State: JobStates.Queued, Trigger: not (JobTriggers.Manual or JobTriggers.Retry) }
            && (await session.GetAsync<Job>(definition.Id, ct))?.Paused == true)
        {
            return (null, null, false);
        }
        var now = DateTime.UtcNow;
        var restarted = run is { State: JobStates.Running, NotBefore: null };
        if (restarted && run.HeartbeatAt >= m_started
            || !immediate && run.NotBefore > now.AddSeconds(1)
            || await session.QueryOver<JobRun>().Where(other => other.JobId == run.JobId && other.State == JobStates.Running && other.Id != run.Id)
                .RowCountAsync(ct) > 0)
        {
            // Running in this process now, waiting for its time, or behind another run of the job.
            return (null, null, false);
        }
        // A heavy job waits for a quiet moment, queued; one an administrator asked for does not.
        if (!immediate && definition.RunsWhenQuiet && run is { State: JobStates.Queued, Trigger: not (JobTriggers.Manual or JobTriggers.Retry) }
            && quiet.Postpone(run.QueuedAt) is { } later)
        {
            run.NotBefore = later;
            await services.GetRequiredService<IMessageContext>().ScheduleAsync(new RunJob(runId, definition.Id), later);
            return (null, null, false);
        }
        if (restarted && run.Attempt >= definition.Retry.Attempts)
        {
            var log = RunLog.From(run.Log);
            log.Add(LogLevel.Error, "Server se restartoval během posledního pokusu.");
            run.Log = log.ToJson();
            run.Error = "Server se restartoval během posledního pokusu.";
            await EndAsync(services, session, run, definition, JobStates.Failed, ct);
            return (null, run, false);
        }
        run.State = JobStates.Running;
        run.Attempt++;
        run.StartedAt ??= now;
        run.HeartbeatAt = now;
        run.NotBefore = null;
        return (run, null, restarted);
    }

    private static async Task EndAsync(IServiceProvider services, ISession session, JobRun run, IJobDefinition definition, string state, CancellationToken ct)
    {
        run.State = state;
        run.FinishedAt = DateTime.UtcNow;
        run.NotBefore = null;
        var requests = services.GetRequiredService<JobRequests>();
        if (state == JobStates.Succeeded && definition.Schedule is JobSchedule.OnceSchedule)
        {
            await requests.CompletedAsync(definition, ct);
        }
        if (state == JobStates.Failed)
        {
            // Decision O8: the wiki administrators, Hub's included (EffectiveWikiAdmin, as JobAccess decides them); the job's key keeps one
            // unread item that counts its failures.
            var admins = await session.QueryOver<UserAccount>().Where(account => account.IsActive)
                .And(Restrictions.Or(Restrictions.Where<UserAccount>(account => account.IsWikiAdmin),
                    Expression.Sql("EXISTS (SELECT 1 FROM \"EffectiveWikiAdmin\" h WHERE h.\"UserId\"={alias}.\"Id\")")))
                .Select(account => account.Id).ListAsync<Guid>(ct);
            services.GetRequiredService<Outbox>().Notify(new NotificationRequested(NotificationKinds.JobFailed, NotificationKinds.JobSubject(run.JobId),
                null, [.. admins], run.JobId));
        }
        await session.FlushAsync(ct);
        await requests.EndedAsync(run.JobId, ct);
    }

    private static async Task BeatAsync(RunContext context, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await context.SaveAsync(progress: null, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    context.Log.LogWarning("Záznam průběhu se nepodařilo uložit ({ErrorType}).", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class RunContext : JobContext
    {
        private readonly IServiceScopeFactory m_scopes;
        private readonly JobRun m_run;
        private readonly CancellationTokenSource m_cancel;
        private readonly Action<JobRun> m_announce;
        private readonly RunLog m_log;
        private string? m_progress;
        private string? m_summary;

        public RunContext(IServiceScopeFactory scopes, JobRun run, IServiceProvider services, ILogger logger, CancellationTokenSource cancel,
            Action<JobRun> announce)
        {
            m_scopes = scopes;
            m_run = run;
            m_cancel = cancel;
            m_announce = announce;
            m_log = RunLog.From(run.Log);
            m_progress = run.Progress;
            m_summary = run.Summary;
            Services = services;
            Log = new RunLogger(logger, m_log);
        }

        public override long RunId => m_run.Id;
        public override string JobId => m_run.JobId;
        public override string Trigger => m_run.Trigger;
        public override string? Reason => m_run.Reason;
        public override string? Key => m_run.Key;
        public override Guid? RequestedBy => m_run.RequestedBy;
        public override int Attempt => m_run.Attempt;
        public override ILogger Log { get; }
        public override IServiceProvider Services { get; }

        public override T? Progress<T>() where T : default
            => m_progress is null ? default : JsonSerializer.Deserialize<T>(m_progress, JsonSerializerOptions.Web);

        public override void Summarize(object summary) => m_summary = JsonSerializer.Serialize(summary, summary.GetType(), JsonSerializerOptions.Web);

        public override async Task<T> StepAsync<T>(Func<ISession, CancellationToken, Task<T>> step, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            T value = default!;
            await SaveAsync(async session =>
            {
                value = await step(session, ct);
                return JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
            }, ct);
            return value;
        }

        /// <summary>The heartbeat, and the end of a step with its new Progress: the time, the log and the summary; a stop request read
        /// here cancels the run once the transaction committed.</summary>
        internal async Task SaveAsync(Func<ISession, Task<string>>? progress, CancellationToken ct)
        {
            var (stop, saved) = await InTransactionAsync(m_scopes, async (_, session) =>
            {
                var value = progress is null ? null : await progress(session);
                var current = await session.GetAsync<JobRun>(m_run.Id, LockMode.Upgrade, ct);
                Write(current, value);
                return (current.CancelRequested, value);
            }, ct);
            m_progress = saved ?? m_progress;
            // A step's end is news for the pages (its summary); a heartbeat is not.
            if (progress is not null)
            {
                m_announce(m_run);
            }
            if (stop)
            {
                await m_cancel.CancelAsync();
            }
        }

        internal void Write(JobRun current, string? progress)
        {
            current.HeartbeatAt = DateTime.UtcNow;
            current.Log = m_log.ToJson();
            current.Summary = m_summary;
            current.Progress = progress ?? current.Progress;
        }
    }

    /// <summary>The ordinary logger of the job, and from Information up also JobRun.Log.</summary>
    private sealed class RunLogger(ILogger inner, RunLog log) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information || inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
            if (logLevel >= LogLevel.Information)
            {
                log.Add(logLevel, formatter(state, exception));
            }
        }
    }
}

/// <summary>A job's failure as it is stored (JobRun.Error and its log), which every job administrator reads: credentials an exception
/// carries (a connection string's password, a bearer token, a URL's user info) are replaced by ***.</summary>
public static partial class JobErrorText
{
    public static string Redact(string text) => Credentials().Replace(text, "${key}***");

    [GeneratedRegex(@"(?<key>\b(?:password|pwd|token|secret|api[_-]?key|access[_-]?token)\s*[=:]\s*|\bBearer\s+)[^;,\s'""]+|(?<key>://[^/\s:@]+:)[^/\s@]+(?=@)",
        RegexOptions.IgnoreCase)]
    private static partial Regex Credentials();
}

/// <summary>An entry of JobRun.Log (§4.5): <c>{ at, level, message }</c>.</summary>
public sealed record JobLogEntry(DateTime At, string Level, string Message);

/// <summary>JobRun.Log (§4.5): at most 500 entries, the first 50 and the last 450 with a „Vynecháno řádků: N“ entry between them.</summary>
internal sealed partial class RunLog
{
    private const int HeadSize = 50;
    private const int TailSize = 450;
    private readonly Lock m_lock = new();
    private readonly List<JobLogEntry> m_head = [];
    private readonly Queue<JobLogEntry> m_tail = new();
    private int m_omitted;

    public static RunLog From(string? json)
    {
        var log = new RunLog();
        foreach (var entry in json is null ? [] : JsonSerializer.Deserialize<JobLogEntry[]>(json, JsonSerializerOptions.Web)!)
        {
            if (log.m_head.Count == HeadSize && log.m_tail.Count == 0 && Omission().Match(entry.Message) is { Success: true } omission)
            {
                log.m_omitted = int.Parse(omission.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                log.Append(entry);
            }
        }
        return log;
    }

    public void Add(LogLevel level, string message) => Append(new JobLogEntry(DateTime.UtcNow, level.ToString(), message));

    public string ToJson()
    {
        lock (m_lock)
        {
            IEnumerable<JobLogEntry> entries = m_head;
            if (m_omitted > 0)
            {
                entries = entries.Append(new JobLogEntry(m_tail.Peek().At, nameof(LogLevel.Information), $"Vynecháno řádků: {m_omitted}"));
            }
            return JsonSerializer.Serialize(entries.Concat(m_tail), JsonSerializerOptions.Web);
        }
    }

    private void Append(JobLogEntry entry)
    {
        lock (m_lock)
        {
            if (m_head.Count < HeadSize)
            {
                m_head.Add(entry);
                return;
            }
            m_tail.Enqueue(entry);
            if (m_tail.Count > TailSize)
            {
                m_tail.Dequeue();
                m_omitted++;
            }
        }
    }

    [GeneratedRegex(@"^Vynecháno řádků: (\d+)$")]
    private static partial Regex Omission();
}

/// <summary>The keys of the log scope every attempt of a run runs in; the stored log keeps the run (AppLog.JobRunId).</summary>
public static class JobLogScope
{
    public const string RunId = "JobRunId", Name = "JobName";
}
