using Atlas.Server.Features.PageHistory.Domain;
using Atlas.Server.Features.PageLinks.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageLinks.Queries;

/// <summary>Link check before publication: the working revision at the expected version, exactly what Publish publishes. It never
/// blocks or changes publication, writes nothing and records no audit.</summary>
public sealed class CheckPublicationHandler(ISession session, PageHistoryAccess pages, PageLinkStore store)
{
    public async Task<PublicationCheck> Handle(CheckPublication query, CancellationToken ct)
    {
        // Authorized like the history: EditContent under shared locks; a reader of the page gets 403 (review S3), anyone else 404.
        var (page, actor) = await pages.RequirePageAsync(query.PageId, false, ct);
        Rules.ExpectVersion(page.Version, query.ExpectedVersion);
        var working = await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null)
            .Select(x => x.Id).SingleOrDefaultAsync<Guid>(ct);
        return new PublicationCheck(page.Version, await store.CheckAsync(actor, page.Id, working, ct));
    }
}
