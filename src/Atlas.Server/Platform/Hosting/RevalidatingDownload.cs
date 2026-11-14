namespace Atlas.Server.Platform.Hosting;

/// <summary>A standard file result whose authorization remains live while bytes are sent.</summary>
public sealed class RevalidatingDownload : IResult
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RenewalMargin = TimeSpan.FromSeconds(1);
    private readonly IResult m_file;
    private readonly Stream m_stream;
    private readonly Func<CancellationToken, Task<DateTime?>> m_authorize;
    private readonly TimeProvider m_clock;
    private readonly (long At, DateTime? Until)? m_proven;

    public RevalidatingDownload(IResult file, Stream stream, Func<CancellationToken, Task<DateTime?>> authorize,
        TimeProvider? timeProvider = null)
    {
        m_file = file;
        m_stream = stream;
        m_authorize = authorize;
        m_clock = timeProvider ?? TimeProvider.System;
    }

    private RevalidatingDownload(IResult file, Stream stream, Func<CancellationToken, Task<DateTime?>> authorize,
        TimeProvider clock, (long At, DateTime? Until) proven) : this(file, stream, authorize, clock)
    {
        m_proven = proven;
    }

    /// <summary>
    /// The transfer of an endpoint that has already authorized the request. <paramref name="first"/> is the answer to the message
    /// <paramref name="authorize"/> sends again, and <paramref name="provenAt"/> is <see cref="TimeProvider.GetTimestamp"/> of
    /// <paramref name="clock"/> read when that answer arrived. The answer is the first lease of the transfer, so the right is not
    /// proven a second time before the first byte. <paramref name="until"/> reads the end of the authority (null is unbounded)
    /// from the first answer and from every later one, so the first lease cannot be bounded differently from its renewals.
    /// </summary>
    public static RevalidatingDownload Proven<T>(IResult file, Stream stream, T first, long provenAt,
        Func<CancellationToken, Task<T>> authorize, Func<T, DateTime?> until, TimeProvider clock)
    {
        return new RevalidatingDownload(file, stream, async ct => until(await authorize(ct)), clock, (provenAt, until(first)));
    }

    public async Task ExecuteAsync(HttpContext context)
    {
        var requestAborted = context.RequestAborted;
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        void Abort() { transfer.Cancel(); context.Abort(); }
        await using var deadline = m_clock.CreateTimer(_ => Abort(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Task? monitor = null;
        try
        {
            var now = m_clock.GetUtcNow().UtcDateTime;
            var cutoff = Adopt(deadline, now);
            if (cutoff is null)
            {
                (cutoff, now) = Schedule(deadline, await m_authorize(transfer.Token));
            }
            context.RequestAborted = transfer.Token;
            monitor = MonitorAsync(deadline, cutoff.Value, now, Abort, transfer.Token);
            await m_file.ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (transfer.IsCancellationRequested)
        {
            context.Abort();
        }
        finally
        {
            await transfer.CancelAsync();
            if (monitor is not null) await monitor;
            context.RequestAborted = requestAborted;
            await m_stream.DisposeAsync();
        }
    }

    /// <param name="now">The reading of the clock <paramref name="cutoff"/> was computed from, for an adopted lease and for one
    /// proven here alike: a second, later reading could find the renewal already missed and wait for the cutoff instead.</param>
    private async Task MonitorAsync(ITimer deadline, DateTime cutoff, DateTime now, Action abort, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var remaining = cutoff - now;
                // Renew before the proven deadline; the independent timer still cuts off a stalled revalidation or write.
                var beforeDeadline = remaining > RenewalMargin ? remaining - RenewalMargin : remaining;
                var delay = beforeDeadline < CheckInterval ? beforeDeadline : CheckInterval;
                if (delay <= TimeSpan.Zero) { abort(); return; }
                await Task.Delay(delay, m_clock, ct);
                var until = await m_authorize(ct);
                ct.ThrowIfCancellationRequested();
                (cutoff, now) = Schedule(deadline, until);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            // Revocation and an unavailable authority source both fail closed after the response may have started.
            abort();
        }
    }

    /// <summary>
    /// Arms the deadline of the authorization the endpoint made itself and returns it. The lease runs from the moment it was
    /// proven, not from the start of the transfer, and its age is read from the monotonic clock: neither the work of the endpoint
    /// in between nor a step of the wall clock puts the first renewal or the cut-off later than CheckInterval after the proof.
    /// Only the end of the authority is a time of day, compared with the wall clock as Schedule compares it. Null leaves the
    /// first authorization to the caller: none was handed over, the lease is due for renewal by now (the endpoint worked for too
    /// long after it, or its authority is about to end), or its stamp is not one this clock has given.
    /// </summary>
    private DateTime? Adopt(ITimer timer, DateTime now)
    {
        if (m_proven is not { } proven) return null;
        var age = m_clock.GetElapsedTime(proven.At);
        // The age is checked before it is used. Below zero the stamp is ahead of this clock, so not one it gave, and would make
        // the lease longer than CheckInterval; from CheckInterval on no time is left, and a far older one does not fit a DateTime.
        if (age < TimeSpan.Zero || age >= CheckInterval) return null;
        var cutoff = now + (CheckInterval - age);
        if (proven.Until is { } end && end < cutoff) cutoff = end;
        // MonitorAsync renews RenewalMargin before the deadline; from then on the renewal belongs before the first byte.
        if (cutoff - now <= RenewalMargin) return null;
        timer.Change(cutoff - now, Timeout.InfiniteTimeSpan);
        return cutoff;
    }

    /// <summary>Arms the deadline of a lease proven just now and returns it with the reading of the clock it was computed from;
    /// the wait for its renewal is computed from the same reading.</summary>
    private (DateTime Cutoff, DateTime Now) Schedule(ITimer timer, DateTime? until)
    {
        var now = m_clock.GetUtcNow().UtcDateTime;
        var cutoff = until is { } end && end < now + CheckInterval ? end : now + CheckInterval;
        var remaining = cutoff - now;
        if (remaining <= TimeSpan.Zero) throw new ApiException(403, "Platnost oprávnění ke stažení skončila.");
        timer.Change(remaining, Timeout.InfiniteTimeSpan);
        return (cutoff, now);
    }
}
