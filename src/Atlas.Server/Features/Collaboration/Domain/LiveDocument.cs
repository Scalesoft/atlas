using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Domain;

/// <summary>Locks the page row and the live state of one generation. Every collaboration write starts here.</summary>
internal static class LiveDocument
{
    public static async Task<PageCollaboration> LockAsync(ISession session, Guid pageId, Guid generation, CancellationToken ct)
    {
        if (await session.GetForUpdateAsync<Page>(pageId, ct) is not { IsTrashed: false })
        {
            throw CollaborationErrors.Stale();
        }
        var state = await session.GetAsync<PageCollaboration>(pageId, LockMode.Upgrade, ct);
        if (state is null || state.Generation != generation)
        {
            throw CollaborationErrors.Stale();
        }
        return state;
    }
}
