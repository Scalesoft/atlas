using Microsoft.AspNetCore.Http;
using Xunit;

namespace Atlas.Server.Tests.Hub;

public sealed class RevalidatingDownloadTests
{
    [Fact]
    public async Task Initial_denial_disposes_the_file_before_starting_the_response()
    {
        var response = new StalledResult();
        var stream = new TrackedStream();
        var result = new RevalidatingDownload(response, stream, _ => throw new ApiException(403, "Access revoked"));
        await Assert.ThrowsAsync<ApiException>(() => result.ExecuteAsync(new DefaultHttpContext()));
        Assert.False(response.Started.Task.IsCompleted);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Deadline_aborts_a_stalled_response_and_disposes_the_file()
    {
        var clock = new DownloadClock();
        var response = new StalledResult();
        var stream = new TrackedStream();
        var result = new RevalidatingDownload(response, stream, _ => Task.FromResult<DateTime?>(clock.Start.AddSeconds(2).UtcDateTime), clock);
        var execution = result.ExecuteAsync(new DefaultHttpContext());
        await response.Started.Task;
        clock.Advance(TimeSpan.FromSeconds(2));
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.Cancelled);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Revocation_is_checked_during_a_stalled_response_with_unbounded_initial_authority()
    {
        var clock = new DownloadClock();
        var response = new StalledResult();
        var stream = new TrackedStream();
        var checks = 0;
        var result = new RevalidatingDownload(response, stream, _ => ++checks == 1
            ? Task.FromResult<DateTime?>(null) : throw new ApiException(403, "Access revoked"), clock);
        var execution = result.ExecuteAsync(new DefaultHttpContext());
        await response.Started.Task;
        clock.Advance(TimeSpan.FromSeconds(4));
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, checks);
        Assert.True(response.Cancelled);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Proven_authority_starts_the_transfer_without_asking_again_and_is_revalidated_from_its_proof()
    {
        var clock = new DownloadClock();
        var response = new StalledResult();
        var stream = new TrackedStream();
        var checks = 0;
        var result = RevalidatingDownload.Proven(response, stream, new DownloadLease(null), clock.GetTimestamp(),
            _ => { checks++; throw new ApiException(403, "Access revoked"); }, lease => lease.Until, clock);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var execution = result.ExecuteAsync(new DefaultHttpContext());
        await response.Started.Task;
        Assert.Equal(0, checks);
        // Four seconds after the proof, not after the start of the transfer.
        clock.Advance(TimeSpan.FromMilliseconds(3500));
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, checks);
        Assert.True(response.Cancelled);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Proof_that_is_no_longer_fresh_is_checked_again_before_the_response_starts()
    {
        var clock = new DownloadClock();
        var response = new StalledResult();
        var stream = new TrackedStream();
        var result = RevalidatingDownload.Proven(response, stream, new DownloadLease(null), clock.GetTimestamp(),
            _ => throw new ApiException(403, "Access revoked"), lease => lease.Until, clock);
        // A proof is fresh while its renewal is still ahead: four seconds after it the renewal is due.
        clock.Advance(TimeSpan.FromSeconds(4));
        await Assert.ThrowsAsync<ApiException>(() => result.ExecuteAsync(new DefaultHttpContext()));
        Assert.False(response.Started.Task.IsCompleted);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Hung_revalidation_aborts_an_unbounded_transfer_within_five_seconds()
    {
        var clock = new DownloadClock();
        var response = new StalledResult();
        var stream = new TrackedStream();
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        async Task<DateTime?> Authorize(CancellationToken ct)
        {
            if (++checks == 1) return null;
            checking.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        }
        using var request = new CancellationTokenSource();
        var result = new RevalidatingDownload(response, stream, Authorize, clock);
        var execution = result.ExecuteAsync(new DefaultHttpContext { RequestAborted = request.Token });
        try
        {
            await response.Started.Task;
            clock.Advance(TimeSpan.FromSeconds(4));
            await checking.Task.WaitAsync(TimeSpan.FromSeconds(2));
            clock.Advance(TimeSpan.FromSeconds(1));
            await execution.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(response.Cancelled);
            Assert.True(stream.Disposed);
        }
        finally
        {
            await request.CancelAsync();
            await execution;
        }
    }

    /// <summary>
    /// An endpoint that has authorized the request itself hands the answer over as the first lease, so the transfer proves
    /// nothing a second time before the first byte. The lease runs from the moment it was proven: a right revoked just after
    /// the start is noticed at the first renewal, four seconds after the proof and so never later than four seconds after the
    /// start, however long the endpoint worked before the transfer began. The age of the proof is read from the monotonic
    /// clock: a wall clock set back while the endpoint works makes the proof no younger and the renewal no later.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    public async Task Authorization_of_the_endpoint_is_the_first_lease_and_a_revocation_after_the_start_is_noticed_at_its_renewal(
        int secondsBeforeTheTransfer, int secondsTheWallClockIsSetBack)
    {
        var clock = new DownloadClock();
        var response = new StalledResult();
        var stream = new TrackedStream();
        var checks = 0;
        var result = RevalidatingDownload.Proven<DateTime?>(response, stream, null, clock.GetTimestamp(),
            _ => { checks++; throw new ApiException(403, "Access revoked"); }, until => until, clock);
        clock.Advance(TimeSpan.FromSeconds(secondsBeforeTheTransfer));
        clock.Step(TimeSpan.FromSeconds(-secondsTheWallClockIsSetBack));
        var execution = result.ExecuteAsync(new DefaultHttpContext());
        // A transfer that proved the right itself would end here on the refusal and never start the response.
        await Task.WhenAny(response.Started.Task, execution);
        Assert.Equal(0, checks);
        clock.Advance(TimeSpan.FromSeconds(4 - secondsBeforeTheTransfer));
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, checks);
        Assert.True(response.Cancelled);
        Assert.True(stream.Disposed);
    }

    /// <summary>
    /// The first lease stands for the authorization before the first byte only while its renewal is still ahead. Once the
    /// endpoint has worked past that point, or when the authority the proof showed ends within the second a renewal is given
    /// (a share link, a grant or a token about to expire), the right is proven again before the first byte, as it is without
    /// a first lease. So it is for a stamp ahead of the clock, which the clock did not give: taken as it is, it would make
    /// the proof younger than new and the lease longer than five seconds.
    /// </summary>
    [Theory]
    [InlineData(4, null, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, null, 1)]
    public async Task First_lease_due_for_renewal_or_stamped_ahead_of_the_clock_is_proven_again_before_the_first_byte(
        int secondsBeforeTheTransfer, int? secondsOfAuthority, int secondsTheStampIsAhead)
    {
        var clock = new DownloadClock();
        var response = new StalledResult();
        var stream = new TrackedStream();
        var result = RevalidatingDownload.Proven(response, stream, End(clock, secondsOfAuthority),
            clock.GetTimestamp() + secondsTheStampIsAhead * clock.TimestampFrequency,
            _ => throw new ApiException(403, "Access revoked"), until => until, clock);
        clock.Advance(TimeSpan.FromSeconds(secondsBeforeTheTransfer));
        // A transfer that trusted the lease would start the stalled response instead of ending on the refusal.
        await Assert.ThrowsAsync<ApiException>(() => result.ExecuteAsync(new DefaultHttpContext()).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(response.Started.Task.IsCompleted);
        Assert.True(stream.Disposed);
    }

    /// <summary>
    /// The deadline of the first lease is armed from the moment it was proven as well, and at the end of the authority the
    /// proof showed when that comes sooner. The renewal starts one second before that deadline and one that hangs is cut off
    /// at it: five seconds after the endpoint authorized, or when the share link, grant or token ends, and not five seconds
    /// after the transfer began or after the proof of an authority that ends earlier.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public async Task Hung_first_renewal_is_cut_off_five_seconds_after_the_authorization_of_the_endpoint_or_at_the_end_of_its_authority(int? secondsOfAuthority)
    {
        var clock = new DownloadClock();
        var response = new StalledResult();
        var stream = new TrackedStream();
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<DateTime?> Authorize(CancellationToken ct)
        {
            checking.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        }
        using var request = new CancellationTokenSource();
        var result = RevalidatingDownload.Proven(response, stream, End(clock, secondsOfAuthority), clock.GetTimestamp(), Authorize, until => until, clock);
        var secondsToTheCutOff = secondsOfAuthority ?? 5;
        clock.Advance(TimeSpan.FromSeconds(1));
        var execution = result.ExecuteAsync(new DefaultHttpContext { RequestAborted = request.Token });
        try
        {
            await response.Started.Task;
            // Two seconds before the cut-off nothing is asked yet; one second before it the renewal starts, and hangs.
            clock.Advance(TimeSpan.FromSeconds(secondsToTheCutOff - 3));
            Assert.False(checking.Task.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(1));
            await checking.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(response.Cancelled);
            clock.Advance(TimeSpan.FromSeconds(1));
            await execution.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(response.Cancelled);
            Assert.True(stream.Disposed);
        }
        finally
        {
            await request.CancelAsync();
            await execution;
        }
    }

    /// <summary>The end of an authority that lasts the given seconds from the start of the clock; null is unbounded.</summary>
    private static DateTime? End(DownloadClock clock, int? seconds)
    {
        return seconds is { } lasting ? clock.Start.AddSeconds(lasting).UtcDateTime : null;
    }

    private sealed class StalledResult : IResult
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public async Task ExecuteAsync(HttpContext context)
        {
            Started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { Cancelled = true; }
        }
    }

    private sealed class TrackedStream : MemoryStream
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class DownloadClock : TimeProvider
    {
        private readonly List<ClockTimer> m_timers = [];
        private DateTimeOffset m_now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        // The monotonic clock: timestamps and timers follow it, and a step of the wall clock does not move it.
        private TimeSpan m_elapsed;
        public DateTimeOffset Start => new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => m_now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => m_elapsed.Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            lock (m_timers) { m_timers.Add(timer); timer.Change(dueTime, period); }
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            ClockTimer[] due;
            lock (m_timers) { m_now += amount; m_elapsed += amount; due = m_timers.Where(t => t.Due is { } at && at <= m_elapsed).ToArray(); }
            foreach (var timer in due) timer.Fire();
        }
        /// <summary>Sets the wall clock, as an operator or a time synchronization would: time itself does not pass.</summary>
        public void Step(TimeSpan amount)
        {
            lock (m_timers) { m_now += amount; }
        }
        private sealed class ClockTimer(DownloadClock clock, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan? Due { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock.m_timers) { Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.m_elapsed + dueTime; }
                return true;
            }
            public void Fire() { Due = null; callback(state); }
            public void Dispose() { lock (clock.m_timers) { Due = null; clock.m_timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
