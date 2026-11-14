using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PageOrganization;

public sealed class PageSubtreeTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";

    [PostgreSqlFact]
    public async Task Descendants_stop_at_a_non_matching_node_while_reachable_walks_the_whole_tree()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_subtree_");
        var writer = await host.SeedUserAsync("writer");
        var spaceId = await host.SeedSpaceAsync("Tree", (writer, SpaceRole.Writer));
        host.Actor.Current = writer;
        var rootId = (await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(spaceId, null, "Root", Rich, "Root")))).Id;
        var childId = (await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(spaceId, null, "Child", Rich, "Child")))).Id;
        var grandChildId = (await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(spaceId, null, "Grandchild", Rich, "Grandchild")))).Id;
        await host.SeedAsync(async session =>
        {
            (await session.GetAsync<Page>(childId)).ParentId = rootId;
            (await session.GetAsync<Page>(grandChildId)).ParentId = childId;
        });
        // The middle node is already in the trash, so the untrashed walk must not reach the grandchild.
        await host.ExecuteAsync("UPDATE \"Page\" SET \"IsTrashed\"=true,\"TrashedAt\"=clock_timestamp(),\"TrashedById\"=\"OwnerId\",\"TrashBatchId\"=gen_random_uuid() WHERE \"Id\"=:id", childId);
        using var session2 = host.OpenSession();
        var root = await session2.GetAsync<Page>(rootId);
        var untrashed = await PageSubtree.DescendantsAsync(session2, root, false, CancellationToken.None);
        Assert.Equal([rootId], untrashed);
        var reachable = await PageSubtree.ReachableAsync(session2, rootId, 1001, CancellationToken.None);
        Assert.Equal(3, reachable.Count);
        Assert.True(await PageSubtree.IsAncestorAsync(session2, grandChildId, rootId, CancellationToken.None));
        Assert.False(await PageSubtree.IsAncestorAsync(session2, rootId, grandChildId, CancellationToken.None));
        Assert.True(await PageSubtree.IsAncestorAsync(session2, rootId, rootId, CancellationToken.None));
    }

    [PostgreSqlFact]
    public async Task Descendants_take_a_level_with_more_pages_than_one_statement_has_parameters()
    {
        // PostgreSQL's wire protocol carries at most 65,535 parameters per statement; the next level of the walk must not bind one per page.
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_subtree_") /* 70,000 pages in the shared database would make every whole-wiki RefreshPageDerivedAsync of other tests walk them in one transaction (out of shared memory) */;
        var (rootId, spaceId) = await SeedWideLevelAsync(host, 70_000);
        using var session = host.OpenSession();
        var root = await session.GetAsync<Page>(rootId);

        var descendants = await PageSubtree.DescendantsAsync(session, root, false, CancellationToken.None);

        Assert.Equal((await AllPageIdsAsync(session, spaceId)).Order().ToArray(), descendants);
    }

    [PostgreSqlFact]
    public async Task Reachable_takes_a_level_with_more_pages_than_one_statement_has_parameters()
    {
        // The only caller limits the walk to 1,001 pages, so it sends at most 1,000 ids; the limit is a parameter of the method, though.
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_subtree_") /* 70,000 pages in the shared database would make every whole-wiki RefreshPageDerivedAsync of other tests walk them in one transaction (out of shared memory) */;
        var (rootId, spaceId) = await SeedWideLevelAsync(host, 70_000);
        using var session = host.OpenSession();

        var reachable = await PageSubtree.ReachableAsync(session, rootId, 100_000, CancellationToken.None);

        Assert.Equal(70_001, reachable.Count);
        Assert.True((await AllPageIdsAsync(session, spaceId)).ToHashSet().SetEquals(reachable));
    }

    /// <summary>A root with <paramref name="children"/> children, created by one statement. SortOrder is set because the check
    /// constraint refuses 0.</summary>
    private static async Task<(Guid RootId, Guid SpaceId)> SeedWideLevelAsync(AtlasTestHost host, int children)
    {
        var owner = await host.SeedUserAsync("owner");
        var spaceId = await host.SeedSpaceAsync("Wide");
        var rootId = Guid.NewGuid();
        await host.SeedAsync(session => session.SaveAsync(new Page { Id = rootId, SpaceId = spaceId, OwnerId = owner.Id }));
        await host.ExecuteAsync($"""
            INSERT INTO "Page"("Id","SpaceId","ParentId","OwnerId","Version","SortOrder")
            SELECT gen_random_uuid(),"SpaceId","Id","OwnerId",1,n FROM "Page" CROSS JOIN generate_series(1,{children}) n WHERE "Id"=:id;
            """, rootId);
        return (rootId, spaceId);
    }

    private static async Task<IList<Guid>> AllPageIdsAsync(NHibernate.ISession session, Guid spaceId) =>
        await session.CreateSQLQuery("""SELECT "Id" FROM "Page" WHERE "SpaceId" = :space""").SetGuid("space", spaceId).ListAsync<Guid>();
}
