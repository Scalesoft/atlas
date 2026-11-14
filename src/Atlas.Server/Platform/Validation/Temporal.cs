namespace Atlas.Server.Platform.Validation;

/// <summary>Boundary arithmetic for temporal rows. PostgreSQL timestamps have microsecond precision; intervals are half-open [from,to).</summary>
public static class Temporal
{
    public static DateTime NextBoundary(DateTime activeFrom, DateTime utcNow)
    {
        if (activeFrom.Kind != DateTimeKind.Utc || utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Temporal boundaries must be UTC.");
        }
        var truncated = TruncateToMicroseconds(utcNow);
        return truncated > activeFrom ? truncated : activeFrom.AddTicks(10);
    }

    /// <summary>The instant at the database's microsecond precision, as UTC (a tick is 100 ns, so the last digit goes), so that a value
    /// handed out equals the row that keeps it.</summary>
    public static DateTime TruncateToMicroseconds(DateTime utc)
    {
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }
}
