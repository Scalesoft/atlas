using Atlas.Server.Features.Spaces.Contracts;
using Atlas.Server.Features.Spaces.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Spaces;

public sealed class SpacesHandlerTests
{
    [PostgreSqlFact]
    public async Task Space_list_is_scoped_to_grants_ordered_by_name_and_reports_the_effective_role()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_space_list_"); // the wiki administrator's list holds the first 200 spaces of the whole installation by name: on the shared host this test's spaces can fall beyond it
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var writer = await host.SeedUserAsync("writer");
        var outsider = await host.SeedUserAsync("outsider");
        var beta = await host.SeedSpaceAsync("Beta", (writer, SpaceRole.Writer));
        var alfa = await host.SeedSpaceAsync("Alfa", (writer, SpaceRole.Reader));
        await host.SeedAsync(async session =>
        {
            var space = await session.GetAsync<Space>(alfa);
            space.Description = "Popis";
        });
        host.Actor.Current = writer;
        var mine = await host.InvokeAsync<AccessibleSpaces>(new GetAccessibleSpaces());
        Assert.Equal(new[] { "Alfa", "Beta" }, mine.Items.Select(x => x.Name).ToArray());
        Assert.Equal("Reader", mine.Items[0].Role);
        Assert.Equal("Popis", mine.Items[0].Description);
        Assert.Equal("Writer", mine.Items[1].Role);
        Assert.Equal("", mine.Items[1].Description);
        Assert.Equal(beta, mine.Items[1].Id);
        host.Actor.Current = admin;
        var all = await host.InvokeAsync<AccessibleSpaces>(new GetAccessibleSpaces());
        var own = all.Items.Where(x => x.Id == alfa || x.Id == beta).ToList(); // a wiki admin sees every space of the shared database
        Assert.Equal(new[] { "Alfa", "Beta" }, own.Select(x => x.Name).ToArray());
        Assert.All(own, item => Assert.Equal("Admin", item.Role));
        host.Actor.Current = outsider;
        Assert.Empty((await host.InvokeAsync<AccessibleSpaces>(new GetAccessibleSpaces())).Items);
        host.Actor.Current = writer;
        await host.ExecuteAsync("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"SpaceId\"=:id AND \"ActiveTo\" IS NULL", beta);
        Assert.Equal(new[] { "Alfa" }, (await host.InvokeAsync<AccessibleSpaces>(new GetAccessibleSpaces())).Items.Select(x => x.Name).ToArray());
    }
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Page_tree_shows_drafts_to_writers_published_titles_to_readers_and_hides_foreign_drafts()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_space_tree_");
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var writer = await host.SeedUserAsync("writer");
        var other = await host.SeedUserAsync("other");
        var reader = await host.SeedUserAsync("reader");
        var space = await host.SeedSpaceAsync("Tree", (writer, SpaceRole.Writer), (other, SpaceRole.Writer), (reader, SpaceRole.Reader));
        var pages = new TestPages(host);
        const string rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var published = await pages.CreateAsync(new(space, null, "Published", rich, "p"), writer, default);
        await pages.PublishAsync(published.Id, new(1), writer, default);
        await pages.SaveAsync(published.Id, new("Working", rich, "w", 1), writer, default);
        var child = await pages.CreateAsync(new(space, published.Id, "Child draft", rich, "c"), writer, default);
        await pages.CreateAsync(new(space, null, "Foreign draft", rich, "f"), other, default);
        // SortOrder is assigned per parent, so a root page and a child can share one value; pin both so the
        // production ORDER BY "SortOrder","Id" has a single answer instead of a Guid tie-break.
        await host.ExecuteAsync("UPDATE \"Page\" SET \"SortOrder\"=1024 WHERE \"Id\"=:id", published.Id);
        await host.ExecuteAsync("UPDATE \"Page\" SET \"SortOrder\"=2048 WHERE \"Id\"=:id", child.Id);
        host.Actor.Current = writer;
        var mine = await host.InvokeAsync<SpacePages>(new GetSpacePages(space));
        Assert.Equal(new[] { "Working", "Child draft" }, mine.Items.Select(x => x.Title).ToArray());
        Assert.Equal(space, mine.Items[0].SpaceId);
        Assert.True(mine.Items[0].IsPublished);
        Assert.Equal(2, mine.Items[0].Version);
        Assert.Equal(published.Id, mine.Items[1].ParentId);
        Assert.False(mine.Items[1].IsPublished);
        Assert.Equal(child.Id, mine.Items[1].Id);
        host.Actor.Current = reader;
        var visible = await host.InvokeAsync<SpacePages>(new GetSpacePages(space));
        Assert.Equal(new[] { "Published" }, visible.Items.Select(x => x.Title).ToArray());
        host.Actor.Current = admin;
        Assert.Equal(3, (await host.InvokeAsync<SpacePages>(new GetSpacePages(space))).Items.Count);
        host.Actor.Current = writer;
        // CK_Page_Trash: a trashed row must carry all three trash columns.
        await host.ExecuteAsync("UPDATE \"Page\" SET \"IsTrashed\"=true,\"TrashedAt\"=now(),\"TrashedById\"=\"OwnerId\",\"TrashBatchId\"=gen_random_uuid() WHERE \"Id\"=:id", child.Id);
        Assert.Single((await host.InvokeAsync<SpacePages>(new GetSpacePages(space))).Items);
        var stranger = await host.SeedUserAsync("stranger");
        host.Actor.Current = stranger;
        var denied = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<SpacePages>(new GetSpacePages(space)));
        Assert.Equal(404, denied.Status);
        Assert.Equal("Prostor nebyl nalezen nebo k němu nemáte přístup.", denied.Message);
    }
    [PostgreSqlFact]
    public async Task Page_tree_hides_an_unpublished_page_from_a_reader_that_owns_it()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_space_tree_owner_");
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var reader = await host.SeedUserAsync("reader");
        var space = await host.SeedSpaceAsync("Tree", (reader, SpaceRole.Reader));
        var pageId = Guid.NewGuid();
        // Seeded rather than created through the API: creating requires Writer, and this case needs a Reader
        // who owns an unpublished page. Legacy dropped it for the reader because the INNER JOIN found no
        // published revision, so the owner clause alone must not bring it back.
        await host.SeedAsync(async session =>
        {
            await session.SaveAsync(new Page { Id = pageId, SpaceId = space, OwnerId = reader.Id });
            await session.FlushAsync();
            await session.SaveAsync(new PageRevision
            {
                Channel = ContentChannels.Web,
                Id = Guid.NewGuid(),
                PageId = pageId,
                Version = 1,
                Title = "Reader draft",
                ContentJson = "{}",
                Markdown = "r",
                AuthorId = reader.Id,
                ActiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        });
        // The reader owns the row, so the owner clause admits it; only PublishedTitle != null keeps it hidden.
        host.Actor.Current = reader;
        Assert.Empty((await host.InvokeAsync<SpacePages>(new GetSpacePages(space))).Items);
        // The row really is there and really is a draft: a wiki admin reads it through the writer branch.
        host.Actor.Current = admin;
        var seen = await host.InvokeAsync<SpacePages>(new GetSpacePages(space));
        Assert.Equal("Reader draft", Assert.Single(seen.Items).Title);
        Assert.Equal(pageId, seen.Items[0].Id);
        Assert.False(seen.Items[0].IsPublished);
    }
}
