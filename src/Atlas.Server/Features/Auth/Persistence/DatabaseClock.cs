using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.Persistence;

/// <summary>
/// The database clock for rows whose CHECK constraints compare timestamps written by different
/// transactions (recovery and activation tokens). HQL current_timestamp() is the dialect's now(),
/// the timestamp of the current transaction; one read per handler keeps a single consistent value.
/// </summary>
public static class DatabaseClock
{
    public static async Task<DateTime> NowAsync(ISession session, CancellationToken ct)
    {
        // HQL scalars need a from clause, and the singleton Workspace row is the one that always
        // exists after setup. No row means no installation: fail loudly rather than hand the caller
        // default(DateTime), which would make every expiry comparison pass.
        var now = await session.CreateQuery("select current_timestamp() from Workspace w where w.Id = 1").UniqueResultAsync<DateTime?>(ct)
            ?? throw new ApiException(503, "Wiki ještě nebyla nastavena.");
        return DateTime.SpecifyKind(now, DateTimeKind.Utc);
    }
}
