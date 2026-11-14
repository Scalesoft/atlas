using NHibernate.Criterion;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ContentImport.Domain;

public static class ImportGuards
{
    /// <summary>Upgrade the space before the actor lock: insert/reorder triggers also acquire this row.</summary>
    public static async Task<UserAccount> RequireAdministratorAsync(ISession session, IContentAccess access, IAtlasLocks locks, Guid? spaceId, CancellationToken ct)
    {
        await locks.AcquireContentSharedAsync(ct);
        if (spaceId is { } id)
        {
            await locks.LockSpaceForUpdateAsync(id, ct);
            // The import writes live pages into its destination; a trashed one must be restored first.
            if ((await session.GetAsync<Space>(id, ct))?.IsTrashed == true)
            {
                throw new ApiException(409, "Cílový prostor importu je v koši. Obnovte ho ve Správě wiki → Koš prostorů a import opakujte.", "space_trashed");
            }
        }
        var actor = await access.LockActorAsync(null, ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(session, actor, ct))
        {
            throw new ApiException(403, "Import může provádět pouze správce wiki.");
        }
        return actor;
    }

    public static async Task RequireQuarantineAsync(ISession session, Guid? spaceId, CancellationToken ct)
    {
        if (spaceId is not { } id)
        {
            return;
        }
        var grants = await session.QueryOver<SpaceGrant>().Where(x => x.SpaceId == id && x.ActiveTo == null).RowCountAsync(ct);
        // A page grant widens access as much as a space grant does.
        var pagesOfSpace = QueryOver.Of<Page>().Where(p => p.SpaceId == id).Select(p => p.Id);
        var pageGrants = await session.QueryOver<ResourceGrant>().Where(x => x.ActiveTo == null)
            .WithSubquery.WhereProperty(x => x.PageId).In(pagesOfSpace)
            .RowCountAsync(ct);
        if (grants + pageGrants > 0)
        {
            throw new ApiException(409, "Importní prostor už má přístupová oprávnění. Další import by mohl rozšířit přístup; použijte nový identifikátor zdroje.");
        }
    }
}
