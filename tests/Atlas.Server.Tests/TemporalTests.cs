using Xunit;
namespace Atlas.Server.Tests;
public class TemporalTests
{
    [Fact] public void Closing_an_interval_uses_strictly_later_UTC_boundary()
    {
        var start = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(Temporal.NextBoundary(start, start) > start);
        Assert.Equal(DateTimeKind.Utc, Temporal.NextBoundary(start, start).Kind);
    }
    [Fact] public void Clock_going_backwards_does_not_overlap_revision_intervals()
    {
        var start = DateTime.UtcNow;
        var boundary = Temporal.NextBoundary(start, start.AddSeconds(-3));
        Assert.True(boundary > start);
    }
    [Fact] public void Non_UTC_clock_is_rejected() => Assert.Throws<ArgumentException>(() => Temporal.NextBoundary(DateTime.UtcNow, DateTime.Now));
}
