using Atlas.Server.Features.Spaces.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Spaces;

public sealed class SpacePageEntryTests
{
    [PostgreSqlFact]
    public async Task Projection_exposes_draft_and_published_titles_of_the_same_page()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_page_entry_");
        var writer = await host.SeedUserAsync("writer");
        var space = await host.SeedSpaceAsync("Tree", (writer, SpaceRole.Writer));
        var pageId = Guid.NewGuid();
        var published = Guid.NewGuid();
        var draft = Guid.NewGuid();
        var trashedId = Guid.NewGuid();
        await host.SeedAsync(async session =>
        {
            await session.SaveAsync(new Page { Id = pageId, SpaceId = space, OwnerId = writer.Id, Version = 2, PublishedRevisionId = published });
            // CK_Page_Trash: a trashed row must carry all three trash columns.
            await session.SaveAsync(new Page
            {
                Id = trashedId,
                SpaceId = space,
                OwnerId = writer.Id,
                IsTrashed = true,
                TrashedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
                TrashedById = writer.Id,
                TrashBatchId = Guid.NewGuid()
            });
            await session.FlushAsync();
            await session.SaveAsync(new PageRevision
            {
                Channel = ContentChannels.Web,
                Id = published,
                PageId = pageId,
                Version = 1,
                Title = "Published",
                ContentJson = "{}",
                Markdown = "p",
                AuthorId = writer.Id,
                ActiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ActiveTo = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
            });
            await session.SaveAsync(new PageRevision
            {
                Channel = ContentChannels.Web,
                Id = draft,
                PageId = pageId,
                Version = 2,
                Title = "Draft",
                ContentJson = "{}",
                Markdown = "d",
                AuthorId = writer.Id,
                ActiveFrom = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
            });
            await session.SaveAsync(new PageRevision
            {
                Channel = ContentChannels.Web,
                Id = Guid.NewGuid(),
                PageId = trashedId,
                Version = 1,
                Title = "Trashed",
                ContentJson = "{}",
                Markdown = "t",
                AuthorId = writer.Id,
                ActiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        });
        using var read = host.OpenSession();
        var rows = await read.QueryOver<SpacePageEntry>().Where(x => x.SpaceId == space).OrderBy(x => x.IsTrashed).Asc.ListAsync();
        Assert.Equal(2, rows.Count);
        var entry = rows.Single(x => x.Id == pageId);
        Assert.Equal("Draft", entry.DraftTitle);
        Assert.Equal("Published", entry.PublishedTitle);
        Assert.True(entry.IsPublished);
        Assert.Equal(2, entry.Version);
        Assert.Equal(writer.Id, entry.OwnerId);
        Assert.False(entry.IsTrashed);
        var trashed = rows.Single(x => x.Id == trashedId);
        Assert.True(trashed.IsTrashed);
        Assert.False(trashed.IsPublished);
        Assert.Equal("Trashed", trashed.DraftTitle);
        Assert.Null(trashed.PublishedTitle);
    }
}
