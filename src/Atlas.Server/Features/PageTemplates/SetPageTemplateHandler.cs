using NHibernate;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Platform.Events;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageTemplates;

public sealed class SetPageTemplateHandler(ISession session, IContentAccess access, IAtlasLocks locks,
    PageReader reader, AccessChanges changes, Outbox outbox)
{
    public async Task<PageDto> Handle(SetPageTemplate command, CancellationToken ct)
    {
        await locks.AcquireContentSharedAsync(ct);
        var spaceId = await session.QueryOver<Page>().Where(x => x.Id == command.PageId)
            .Select(x => x.SpaceId).SingleOrDefaultAsync<Guid>(ct);
        if (spaceId == Guid.Empty)
            throw new ApiException(404, "Stránka nebyla nalezena.");
        try
        {
            await access.LockActorAsync(spaceId, ct);
            await access.RequireSpaceAsync(spaceId, SpaceRole.Reader, ct);
        }
        catch (ApiException error) when (error.Status == 404)
        {
            // The space's own 404 text, from its lock or from the role, would tell an account that the page id exists.
            throw new ApiException(404, "Stránka nebyla nalezena.");
        }
        var page = await session.GetForUpdateAsync<Page>(command.PageId, ct);
        if (page is null || page.IsTrashed || page.SpaceId != spaceId)
            throw new ApiException(404, "Stránka nebyla nalezena.");
        // The space role alone does not read another author's unpublished draft, and the answer below carries the page.
        await access.RequirePageAsync(page, AccessOperation.ManageAccess, ct);
        // Only after the page's decision: who may read the page is told that the flag needs the space Admin (403, review S3).
        await access.RequireSpaceAsync(spaceId, SpaceRole.Admin, ct);
        if (command.IsTemplate && !page.IsTemplate)
        {
            var child = await session.QueryOver<Page>().Where(x => x.ParentId == page.Id && !x.IsTrashed)
                .Select(x => x.Id).Take(1).SingleOrDefaultAsync<Guid>(ct);
            if (child != Guid.Empty)
                throw new ApiException(400, "Stránka s podstránkami nemůže být šablonou.");
        }
        if (page.IsTemplate != command.IsTemplate)
        {
            page.IsTemplate = command.IsTemplate;
            // Templates leave the AI index and the relations: the derived rows rebuild from the page, nobody is told.
            outbox.ContentRebuilt(page.Id, PagesAtlasModule.ContentType);
            await session.CreateSQLQuery("UPDATE \"SpaceOrganization\" SET \"Version\"=\"Version\"+1 WHERE \"SpaceId\"=:space")
                .SetParameter("space", spaceId).ExecuteUpdateAsync(ct);
            changes.Changed();
        }
        return await reader.ReadEditedWithoutProofAsync(page.Id, ct);
    }
}
