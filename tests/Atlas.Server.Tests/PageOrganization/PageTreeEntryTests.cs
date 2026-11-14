using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PageOrganization;

public sealed class PageTreeEntryTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";

    [PostgreSqlFact]
    public async Task Projection_exposes_working_and_published_titles_trash_columns_and_the_tree_version()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_tree_entry_");
        var writer = await host.SeedUserAsync("writer");
        var spaceId = await host.SeedSpaceAsync("Tree", (writer, SpaceRole.Writer));
        host.Actor.Current = writer;
        var publishedPage = await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(spaceId, null, "Published", Rich, "Published")));
        var publishedId = publishedPage.Id;
        await host.InvokeAsync<PageDto>(new PublishPage(publishedId, new PublishPageRequest(publishedPage.Version)));
        var draftId = (await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(spaceId, null, "Draft", Rich, "Draft")))).Id;
        using var check = host.OpenSession();
        var rows = await check.QueryOver<PageTreeEntry>().Where(x => x.SpaceId == spaceId)
            .OrderBy(x => x.SortOrder).Asc.ListAsync();
        Assert.Equal(2, rows.Count);
        var published = rows.Single(x => x.Id == publishedId);
        Assert.True(published.IsPublished);
        Assert.Equal("Published", published.WorkingTitle);
        Assert.Equal("Published", published.PublishedTitle);
        Assert.False(published.IsTrashed);
        Assert.Null(published.TrashedAt);
        var draft = rows.Single(x => x.Id == draftId);
        Assert.False(draft.IsPublished);
        Assert.Null(draft.PublishedTitle);
        Assert.Equal(writer.Id, draft.OwnerId);
        Assert.True(await check.QueryOver<SpaceOrganization>().Where(x => x.SpaceId == spaceId).Select(x => x.Version).SingleOrDefaultAsync<long>() > 0);
    }
}
