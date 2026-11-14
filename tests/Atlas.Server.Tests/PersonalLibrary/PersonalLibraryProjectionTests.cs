using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.PersonalLibrary.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PersonalLibrary;

public sealed class PersonalLibraryProjectionTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";

    [PostgreSqlFact]
    public async Task Page_access_resolves_direct_group_and_admin_roles_and_the_page_entry_carries_both_titles()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_library_projection_");
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var writer = await host.SeedUserAsync("writer");
        var member = await host.SeedUserAsync("member");
        var outsider = await host.SeedUserAsync("outsider");
        var groupId = await host.SeedGroupAsync("Editors", member);
        var spaceId = await host.SeedSpaceAsync("Library", (writer, SpaceRole.Reader));
        await host.SeedAsync(async session =>
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = spaceId, GroupId = groupId, Role = 2 }));
        // Created by member (Writer via the group grant): writer only holds Reader here, exactly the role split under test.
        host.Actor.Current = member;
        var page = await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(spaceId, null, "Draft title", Rich, "Draft title")));
        var pageId = page.Id;
        await host.InvokeAsync<PageDto>(new PublishPage(pageId, new PublishPageRequest(page.Version)));

        using var check = host.OpenSession();
        Assert.Equal(3, await check.QueryOver<PageAccessEntry>().Where(x => x.UserId == admin.Id && x.PageId == pageId).Select(x => x.Role).SingleOrDefaultAsync<int>());
        Assert.Equal(1, await check.QueryOver<PageAccessEntry>().Where(x => x.UserId == writer.Id && x.PageId == pageId).Select(x => x.Role).SingleOrDefaultAsync<int>());
        Assert.Equal(2, await check.QueryOver<PageAccessEntry>().Where(x => x.UserId == member.Id && x.PageId == pageId).Select(x => x.Role).SingleOrDefaultAsync<int>());
        Assert.Equal(0, await check.QueryOver<PageAccessEntry>().Where(x => x.UserId == outsider.Id).RowCountAsync());

        var entry = await check.QueryOver<LibraryPageEntry>().Where(x => x.Id == pageId).SingleOrDefaultAsync();
        Assert.Equal("Library", entry.SpaceName);
        Assert.True(entry.IsPublished);
        Assert.Equal("Draft title", entry.WorkingTitle);
        Assert.Equal("Draft title", entry.PublishedTitle);
        Assert.NotNull(entry.PublishedAt);
        Assert.Equal(DateTimeKind.Utc, entry.WorkingActiveFrom.Kind);
        Assert.Null(entry.ParentId);
    }
}
