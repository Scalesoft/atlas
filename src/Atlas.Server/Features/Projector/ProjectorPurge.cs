using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Projector;

/// <summary>The requirement index and catalog marks of permanently deleted pages, with the module on or off (ARCH3-2): the index
/// triggers run either way, so the rows exist either way. The catalog's event history stays (ProjectorCatalogEvent, audit).</summary>
public sealed class ProjectorPurge(ISession session) : IContentPurgeParticipant
{
    public string? Counted => null;

    public async Task<int> PurgeAsync(Guid spaceId, IReadOnlyCollection<Guid> pageIds, Guid? batchId, Guid actorId, CancellationToken ct)
    {
        if (pageIds.Count == 0)
        {
            return 0;
        }
        await session.CreateSQLQuery($"""
            DELETE FROM "ProjectorCatalogPage" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "ProjectorIndexedRevision" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "ProjectorRequirementIndex" WHERE "PageId" = {GuidList.Any(":pages")};
            """).SetParameter("pages", GuidList.Param(pageIds)).ExecuteUpdateAsync(ct);
        return 0;
    }
}
