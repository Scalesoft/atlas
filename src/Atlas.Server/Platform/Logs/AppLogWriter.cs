using Atlas.Server.Platform.Live;
using NHibernate;

namespace Atlas.Server.Platform.Logs;

/// <summary>
/// Stores what <see cref="AppLogQueue"/> holds: a short pause after the first line gathers a batch, which goes in with one INSERT per
/// <see cref="MaxBatch"/> rows, redacted (<see cref="AppLogRedaction"/>) and capped; at shutdown the rest. Until the database is ready the
/// lines wait in the queue (the start's own included), and a full queue's drops come in as one warning line. Its own lines go to the
/// console only (<see cref="AppLogOptions.MinimumFor"/>): a failing write must not feed itself. A stored batch tells the live viewers
/// (<see cref="LogLiveChannel"/>), throttled, without a line in the message.
/// </summary>
public sealed class AppLogWriter(AppLogQueue queue, AtlasDatabase database, ILogger<AppLogWriter> logger, LiveRelay? relay = null,
    LiveThrottle? throttle = null) : BackgroundService
{
    /// <summary>11 parameters a row.</summary>
    public const int MaxBatch = 500;

    public const int MaxMessage = 4000, MaxExceptionMessage = 2000, MaxException = 16_000;

    private static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(250);

    private long m_reported;

    // The most severe level stored since the last signal went out (-1: none); the signal takes and resets it.
    private int m_written = -1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                await Task.Delay(database.Initialized ? Gather : TimeSpan.FromSeconds(1), stoppingToken);
                if (database.Initialized)
                {
                    await FlushAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (database.Initialized)
        {
            await FlushAsync(cancellationToken);
        }
    }

    /// <summary>Writes everything queued now; a batch that fails is dropped and told to the console.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var batch = new List<AppLogEntry>(MaxBatch);
        while (queue.Reader.TryRead(out var entry))
        {
            batch.Add(entry);
            if (batch.Count == MaxBatch)
            {
                await WriteAsync(batch, ct);
                batch.Clear();
            }
        }
        var dropped = queue.Dropped;
        if (dropped > m_reported)
        {
            batch.Add(new AppLogEntry(DateTime.UtcNow, LogLevel.Warning, typeof(AppLogWriter).FullName!, null,
                $"Fronta logu byla plná: {dropped - m_reported} záznamů se nezapsalo.", null, null, null));
            m_reported = dropped;
        }
        if (batch.Count > 0)
        {
            await WriteAsync(batch, ct);
        }
    }

    // Native SQL, category 4 of docs/architecture.md: one multi-row INSERT per batch of an append-only operational log, outside any request session.
    private async Task WriteAsync(List<AppLogEntry> batch, CancellationToken ct)
    {
        try
        {
            using var session = database.OpenSession();
            var values = string.Join(",", batch.Select((_, i) => $"(:at{i},:level{i},:category{i},:event{i},:message{i},:type{i},:error{i},:exception{i},:trace{i},:user{i},:run{i})"));
            var query = session.CreateSQLQuery($"""
                INSERT INTO "AppLog"("At","Level","Category","EventId","Message","ExceptionType","ExceptionMessage","Exception","TraceId","UserId","JobRunId") VALUES {values}
                """);
            for (var i = 0; i < batch.Count; i++)
            {
                var entry = batch[i];
                var exception = entry.Exception;
                query.SetParameter("at" + i, entry.At, NHibernateUtil.UtcDateTime)
                    .SetParameter("level" + i, (short)entry.Level, NHibernateUtil.Int16)
                    .SetParameter("category" + i, Cap(entry.Category, 200), NHibernateUtil.String)
                    .SetParameter("event" + i, entry.EventId, NHibernateUtil.Int32)
                    .SetParameter("message" + i, Clean(entry.Message, MaxMessage), NHibernateUtil.String)
                    .SetParameter("type" + i, exception is null ? null : Cap(exception.GetType().FullName ?? exception.GetType().Name, 300), NHibernateUtil.String)
                    .SetParameter("error" + i, exception is null ? null : Clean(exception.Message, MaxExceptionMessage), NHibernateUtil.String)
                    .SetParameter("exception" + i, exception is null ? null : Clean(exception.ToString(), MaxException), NHibernateUtil.String)
                    .SetParameter("trace" + i, entry.TraceId is null ? null : Cap(entry.TraceId, 64), NHibernateUtil.String)
                    .SetParameter("user" + i, entry.UserId, NHibernateUtil.Guid)
                    .SetParameter("run" + i, entry.JobRunId, NHibernateUtil.Int64);
            }
            await query.ExecuteUpdateAsync(ct);
            Announce(batch.Max(entry => entry.Level));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Writing {Count} lines of the application log failed; they are dropped", batch.Count);
        }
    }

    private void Announce(LogLevel level)
    {
        if (relay is null || throttle is null)
        {
            return;
        }
        int seen;
        while ((seen = Volatile.Read(ref m_written)) < (int)level && Interlocked.CompareExchange(ref m_written, (int)level, seen) != seen)
        {
        }
        throttle.Post(LogLiveChannel.Prefix, () =>
        {
            var most = Interlocked.Exchange(ref m_written, -1);
            return most < 0 ? Task.CompletedTask
                : relay.PublishAsync(LogLiveChannel.Name, LogLiveChannel.Event, new LogsWritten(((LogLevel)most).ToString().ToLowerInvariant()));
        });
    }

    /// <summary>Capped (before the redaction too, which then reads a bounded text), redacted, without the NUL PostgreSQL refuses in text.</summary>
    private static string Clean(string text, int max) => Cap(AppLogRedaction.Redact(Cap(text, max).Replace('\0', ' ')), max);

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
