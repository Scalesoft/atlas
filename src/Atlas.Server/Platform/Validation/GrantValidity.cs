namespace Atlas.Server.Platform.Validation;

/// <summary>The request checks of the space, page, vault and secret grant handlers: exactly one user or group, and finite validity
/// bounds at the database's precision with the start before the end (docs/grant-validity.md). They look only at the request values;
/// each handler's Handle runs them before it reads or locks anything. The approval of an access request calls
/// VaultGrantHandler.ApplyAsync directly and skips them. Whether the actor may grant and whether the subject exists stay with the
/// handlers (<see cref="GrantSubjects"/> for the page and vault ones).</summary>
public static class GrantValidity
{
    public static void RequireOneSubject(Guid? userId, Guid? groupId)
    {
        if ((userId is null) == (groupId is null))
        {
            throw new ApiException(400, "Vyberte právě jednoho uživatele nebo skupinu.");
        }
    }

    /// <summary>The bounds as UTC at microsecond precision; null stays unbounded. Infinite instants are refused (the first start,
    /// then the end), and so is a start that is not before the end.</summary>
    public static (DateTime? StartAt, DateTime? ExpiresAt) Normalize(DateTimeOffset? startAt, DateTimeOffset? expiresAt)
    {
        var start = Normalize(startAt);
        var end = Normalize(expiresAt);
        if (start is not null && end is not null && start >= end)
        {
            throw new ApiException(400, "Začátek platnosti musí být před koncem platnosti.");
        }
        return (start, end);
    }

    private static DateTime? Normalize(DateTimeOffset? value)
    {
        if (value is null)
        {
            return null;
        }
        var utc = value.Value.UtcDateTime;
        // Npgsql maps DateTime min/max to PostgreSQL infinities. Business validity permits only finite instants.
        if (utc.Ticks < 10 || utc == DateTime.MaxValue)
        {
            throw new ApiException(400, "Zadejte konečné datum platnosti.");
        }
        return Temporal.TruncateToMicroseconds(utc);
    }
}
