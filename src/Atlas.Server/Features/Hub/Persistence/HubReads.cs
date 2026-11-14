using Atlas.Server.Features.Hub.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Hub.Persistence;

public static class HubReads
{
    public static Task<HubConnectionRevision?> CurrentAsync(ISession session, CancellationToken ct) =>
        session.QueryOver<HubConnectionRevision>().Where(x => x.ActiveTo == null).SingleOrDefaultAsync(ct)!;

    public static async Task<DateTime> NowAsync(ISession session, CancellationToken ct) =>
        DateTime.SpecifyKind(await session.CreateSQLQuery("SELECT statement_timestamp()").UniqueResultAsync<DateTime>(ct), DateTimeKind.Utc);

    public static string IdentityLock(Guid connection, Guid identity) => $"hub-sync:{connection:N}:{identity:N}";
}
