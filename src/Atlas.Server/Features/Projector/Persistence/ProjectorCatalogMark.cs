using Atlas.Server.Features.Projector.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Projector.Persistence;

/// <summary>The catalog flag of every page read (review C3-2): the web shows the chip without loading the space's catalog.</summary>
public sealed class ProjectorCatalogMark(ISession session) : IPageCatalogMark
{
    public async Task<bool> IsCatalogAsync(Guid pageId, CancellationToken ct) => await session.GetAsync<ProjectorCatalogPage>(pageId, ct) is not null;
}
