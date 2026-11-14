using Atlas.Server.Platform.Events;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Entities.Commands;

/// <summary>The catalog's full rebuild in one transaction (<see cref="EntityCatalogRebuildJob"/>): ContentRevised <c>rebuild</c> of every
/// resource that holds a contact: every contact, and every page whose working or published revision mentions one.</summary>
public sealed record AnnounceCatalogRebuild;

public sealed class AnnounceCatalogRebuildHandler(ISession session, Outbox outbox)
{
    /// <returns>How many resources were announced.</returns>
    public async Task<int> Handle(AnnounceCatalogRebuild command, CancellationToken ct)
    {
        var resources = await session.CreateSQLQuery("""
            SELECT r."Id", r."TypeId"
            FROM "ContentResource" r
            WHERE r."TypeId" = 'atlas.contact'
               OR r."TypeId" = 'atlas.page' AND EXISTS (SELECT 1 FROM "Page" p JOIN "PageRevision" w ON w."PageId" = p."Id"
                    AND (w."ActiveTo" IS NULL OR w."Id" = p."PublishedRevisionId")
                  WHERE p."Id" = r."Id" AND w."ContentJson" LIKE '%"contact%')
            ORDER BY r."Id"
            """)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("TypeId", NHibernateUtil.String).ListAsync<object[]>(ct);
        foreach (var resource in resources)
        {
            outbox.ContentRebuilt((Guid)resource[0], (string)resource[1]);
        }
        return resources.Count;
    }
}
