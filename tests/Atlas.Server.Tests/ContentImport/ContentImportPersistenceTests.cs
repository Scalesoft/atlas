using Atlas.Server.Features.ContentImport.Domain;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.ContentImport;

public sealed class ContentImportPersistenceTests
{
    private const string Content = "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}}";

    [PostgreSqlFact]
    public async Task Import_identities_and_checkpoints_round_trip_through_class_mappings()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_import_map_");
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var spaceId = await host.SeedSpaceAsync("Imported");
        host.Actor.Current = admin;
        var page = await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(spaceId, null, "Article", Content, "Article")));
        var pageId = page.Id;
        var sourceId = Guid.NewGuid();
        var mapId = Guid.NewGuid();
        var revisionId = Guid.Empty;
        await host.SeedAsync(async session =>
        {
            revisionId = await session.QueryOver<PageRevision>().Where(x => x.PageId == pageId && x.ActiveTo == null)
                .Select(x => x.Id).SingleOrDefaultAsync<Guid>();
            await session.SaveAsync(new WikiImportSource { Id = sourceId, SourceKey = TestLogin.For("wiki") });
            await session.FlushAsync();
            await session.SaveAsync(new WikiImportDestination { Id = Guid.NewGuid(), SourceId = sourceId, SpaceId = spaceId, ActiveFrom = DateTime.UtcNow });
            await session.SaveAsync(new WikiImportPage { Id = mapId, SourceId = sourceId, SourcePath = "article.md", PageId = pageId });
            await session.FlushAsync();
            await session.SaveAsync(new WikiImportCheckpoint { Id = Guid.NewGuid(), ImportPageId = mapId, RevisionId = revisionId, SourceSha256 = new string('b', 64), ActiveFrom = DateTime.UtcNow });
            await session.SaveAsync(new WikiImportBatch
            {
                Id = Guid.NewGuid(), SourceId = sourceId, CreatedBy = admin.Id, CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(24), ArchiveSha256 = new string('c', 64), OptionsJson = "{}", ReportJson = "{}"
            });
        });
        using var check = host.OpenSession();
        var checkpoint = await check.QueryOver<WikiImportCheckpoint>().Where(x => x.ImportPageId == mapId && x.ActiveTo == null).SingleOrDefaultAsync();
        Assert.Equal(revisionId, checkpoint.RevisionId);
        Assert.Equal(DateTimeKind.Utc, checkpoint.ActiveFrom.Kind);
        var batch = await check.QueryOver<WikiImportBatch>().Where(x => x.SourceId == sourceId).SingleOrDefaultAsync();
        Assert.Equal(DateTimeKind.Utc, batch.ExpiresAt.Kind);
        Assert.Null(batch.CompletedAt);
        Assert.Equal(spaceId, (await check.QueryOver<WikiImportDestination>().Where(x => x.SourceId == sourceId && x.ActiveTo == null).SingleOrDefaultAsync()).SpaceId);
    }
}
