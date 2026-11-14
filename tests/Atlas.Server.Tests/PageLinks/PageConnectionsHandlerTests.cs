using Atlas.Server.Features.PageLinks.Contracts;
using Atlas.Server.Features.PageLinks.Queries;
using Xunit;
using static Atlas.Server.Tests.PageLinks.PageLinksFixture;

namespace Atlas.Server.Tests.PageLinks;

public sealed class PageConnectionsHandlerTests
{
    [PostgreSqlFact]
    public async Task Links_here_lists_only_sources_the_reader_may_read_and_counts_only_those()
    {
        await using var f = await PageLinksFixture.CreateAsync();
        var target = await f.CreatePublished("Target");
        var link = Doc(Ref(target.Id));
        var visible = await f.CreatePublished("Visible", link);
        // A pending rename is the writer's draft: links here always show the published title.
        await f.Save(visible, "Renamed in draft", link);
        var sharedSpace = await f.Host.SeedSpaceAsync("Shared", (f.Reader, SpaceRole.Reader));
        var elsewhere = await f.CreatePublished("Aaa elsewhere", link, sharedSpace, f.Admin);
        await f.CreatePublished("Foreign", link, f.ForeignSpaceId, f.Admin);
        var restricted = await f.CreatePublished("Restricted", link);
        await f.Execute("UPDATE \"Page\" SET \"OwnAccess\"=true WHERE \"Id\"=:id", restricted.Id);
        var trashed = await f.CreatePublished("Trashed", link);
        await f.Trash(trashed);
        var template = await f.CreatePublished("Template", link);
        await f.Execute("UPDATE \"Page\" SET \"IsTemplate\"=true WHERE \"Id\"=:id", template.Id);
        await f.Create("Never published", link);

        var reader = (await f.As<PageConnections>(f.Reader, new GetPageConnections(target.Id, 10))).LinksHere;
        Assert.Equal(2, reader.Total);
        // The target's space first, then by title.
        Assert.Equal([new LinkingPage(visible.Id, f.SpaceId, "Handbook", "Visible"), new LinkingPage(elsewhere.Id, sharedSpace, "Shared", "Aaa elsewhere")],
            reader.Items);

        var admin = (await f.As<PageConnections>(f.Admin, new GetPageConnections(target.Id, 10))).LinksHere;
        Assert.Equal(4, admin.Total);
        Assert.Equal(["Restricted", "Visible", "Aaa elsewhere", "Foreign"], admin.Items.Select(x => x.Title));
        var first = (await f.As<PageConnections>(f.Admin, new GetPageConnections(target.Id, 1))).LinksHere;
        Assert.Equal(4, first.Total);
        Assert.Equal(restricted.Id, Assert.Single(first.Items).Id);
    }

    [PostgreSqlFact]
    public async Task Links_here_is_missing_for_an_unreadable_target_and_a_revoked_actor()
    {
        await using var f = await PageLinksFixture.CreateAsync();
        var hidden = await f.CreatePublished("Hidden target", spaceId: f.ForeignSpaceId, owner: f.Admin);
        await f.CreatePublished("Source", Doc(Ref(hidden.Id)));
        await Status(404, () => f.As<PageConnections>(f.Reader, new GetPageConnections(hidden.Id, 10)));
        await Status(404, () => f.As<PageConnections>(f.Writer, new GetPageConnections(Guid.NewGuid(), 10)));
        var target = await f.CreatePublished("Target");
        await f.CreatePublished("Linking", Doc(Ref(target.Id)));
        foreach (var limit in new[] { 0, 51 })
            await Status(400, () => f.As<PageConnections>(f.Reader, new GetPageConnections(target.Id, limit)));
        Assert.Equal(1, (await f.As<PageConnections>(f.Reader, new GetPageConnections(target.Id, 10))).LinksHere.Total);

        await f.Execute("UPDATE \"UserAccount\" SET \"SecurityStamp\"='revoked' WHERE \"Id\"=:id", f.Reader.Id);
        await Status(401, () => f.As<PageConnections>(f.Reader, new GetPageConnections(target.Id, 10)));
        await f.Trash(target);
        await Status(404, () => f.As<PageConnections>(f.Writer, new GetPageConnections(target.Id, 10)));
    }

    private static async Task Status(int status, Func<Task> call)
    {
        var error = await Assert.ThrowsAsync<ApiException>(call);
        Assert.Equal(status, error.Status);
    }
}
