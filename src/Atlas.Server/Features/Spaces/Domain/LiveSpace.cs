using NHibernate;
using NHibernate.Criterion;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Spaces.Domain;

/// <summary>The one rule for a space in the space trash: it reads as missing, for the wiki admin too, so every caller answers 404
/// exactly as for a space that never existed, lists leave it out (<see cref="Query"/>) and so do the rows of its objects
/// (<see cref="Ids"/>). Raw SQL tests <c>"TrashedAt" IS NULL</c> on the joined space.</summary>
public static class LiveSpace
{
    public static async Task<Space?> FindAsync(ISession session, Guid id, CancellationToken ct, LockMode? lockMode = null)
    {
        var space = await session.GetAsync<Space>(id, lockMode ?? LockMode.None, ct);
        return space is { TrashedAt: null } ? space : null;
    }

    public static async Task<bool> ExistsAsync(ISession session, Guid id, CancellationToken ct) => await FindAsync(session, id, ct) is not null;

    /// <summary>The spaces outside the trash.</summary>
    public static IQueryOver<Space, Space> Query(ISession session) => session.QueryOver<Space>().Where(x => x.TrashedAt == null);

    /// <summary>The ids of the spaces outside the trash, a subquery for <c>SpaceId IN …</c>.</summary>
    public static QueryOver<Space> Ids() => QueryOver.Of<Space>().Where(x => x.TrashedAt == null).Select(x => x.Id);
}
