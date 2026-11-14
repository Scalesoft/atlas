using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Domain;

public static class TreeVersion
{
    public static Task<long> ReadAsync(ISession session, Guid spaceId, CancellationToken ct)
    {
        return session.QueryOver<SpaceOrganization>().Where(x => x.SpaceId == spaceId).Select(x => x.Version).SingleOrDefaultAsync<long>(ct);
    }

    /// <summary>The counter moves with every change of the space's structure (a page or board created, moved, trashed or restored by anyone), so
    /// a client's number goes stale as soon as someone else touches the tree: the 409 carries the current one for the retry.</summary>
    public static async Task ExpectAsync(ISession session, Guid spaceId, long expected, CancellationToken ct)
    {
        var current = await ReadAsync(session, spaceId, ct);
        if (expected <= 0 || current != expected)
        {
            throw new ApiException(409, "Struktura prostoru se mezitím změnila. Obnovte strom a zopakujte akci.", "tree_conflict",
                new Dictionary<string, object?> { ["currentTreeVersion"] = current });
        }
    }
}
