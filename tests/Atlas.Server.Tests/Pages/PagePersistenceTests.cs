using NHibernate;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Pages;

public sealed class PagePersistenceTests
{
    [PostgreSqlFact]
    public async Task Page_revision_and_publication_round_trip_with_UTC_boundaries()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_page_map_");
        var owner = await host.SeedUserAsync("owner");
        var space = await host.SeedSpaceAsync("Mapping", (owner, SpaceRole.Writer));
        var pageId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var publicationId = Guid.NewGuid();
        var activeFrom = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await host.SeedAsync(async session =>
        {
            await session.SaveAsync(new Page { Id = pageId, SpaceId = space, OwnerId = owner.Id });
            await session.FlushAsync();
            await session.SaveAsync(new PageRevision
            {
                Channel = ContentChannels.Web,
                Id = revisionId,
                PageId = pageId,
                Version = 1,
                Title = "Mapped",
                ContentJson = "{}",
                Markdown = "m",
                AuthorId = owner.Id,
                ActiveFrom = activeFrom
            });
            await session.SaveAsync(new PagePublication { Channel = ContentChannels.Web, Id = publicationId, PageId = pageId, RevisionId = revisionId, PublisherId = owner.Id, ActiveFrom = activeFrom });
        });
        using var read = host.OpenSession();
        var page = await read.GetAsync<Page>(pageId);
        Assert.Equal(space, page.SpaceId);
        Assert.Equal(owner.Id, page.OwnerId);
        Assert.Equal(1, page.Version);
        Assert.False(page.IsTrashed);
        Assert.Null(page.TrashedAt);
        var revision = await read.GetAsync<PageRevision>(revisionId);
        Assert.Equal("Mapped", revision.Title);
        Assert.Equal(activeFrom, revision.ActiveFrom);
        Assert.Equal(DateTimeKind.Utc, revision.ActiveFrom.Kind);
        Assert.Null(revision.ActiveTo);
        var publication = await read.GetAsync<PagePublication>(publicationId);
        Assert.Equal(pageId, publication.PageId);
        Assert.Equal(revisionId, publication.RevisionId);
        Assert.Equal(DateTimeKind.Utc, publication.ActiveFrom.Kind);
    }
}
