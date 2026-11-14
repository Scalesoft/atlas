using Atlas.Server.Features.KnowledgeMaintenance;
using Atlas.Server.Features.SpaceAi.Commands;
using Atlas.Server.Features.SpaceAi.Contracts;
using Atlas.Server.Tests.PageLinks;
using Xunit;

namespace Atlas.Server.Tests.KnowledgeMaintenance;

public sealed class DuplicatePagesTests
{
    [PostgreSqlFact]
    public async Task Only_admins_see_current_published_pairs_in_their_included_space()
    {
        await using var f = await RelatedFixture.CreateAsync();
        var first = await f.PublishAsync("První", RelatedFixture.Topic(12, 0));
        var second = await f.PublishAsync("Druhá", RelatedFixture.Topic(12, 0));
        await f.PublishAsync("Jiný prostor", RelatedFixture.Topic(12, 0), f.ExcludedSpaceId);
        await f.Index.ActiveIndexAsync();
        // Similarity is precomputed by a separately tested worker; this test owns visibility and pair normalization.
        await f.Index.Host.SeedAsync(s => s.CreateSQLQuery("""UPDATE "PageNeighbor" SET "Similarity"=1""").ExecuteUpdateAsync());
        var list = await f.Index.As<DuplicatePageList>(f.Admin, new GetDuplicatePages(f.Index.SpaceId));
        var pair = Assert.Single(list.Items);
        Assert.Equal(new[] { first.Id, second.Id }.ToHashSet(), new[] { pair.PageId, pair.OtherPageId }.ToHashSet());
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() =>
            f.Index.As<DuplicatePageList>(f.Reader, new GetDuplicatePages(f.Index.SpaceId)))).Status);

        await f.Index.As<SpaceAiSettingsView>(f.Admin,
            new SaveSpaceAiSettings(f.Index.SpaceId, new SaveSpaceAiSettingsRequest(false, null)));
        Assert.Empty((await f.Index.As<DuplicatePageList>(f.Admin, new GetDuplicatePages(f.Index.SpaceId))).Items);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.Index.As<bool>(f.Admin,
            new DismissDuplicatePages(f.Index.SpaceId, first.Id, second.Id)))).Status);
        Assert.Equal(0, await f.Index.CountAsync("""SELECT count(*) FROM "AiDuplicateDismissal" """));
    }

    [PostgreSqlFact]
    public async Task Stale_publications_are_filtered_on_both_ends_before_the_limit()
    {
        await using var f = await RelatedFixture.CreateAsync();
        var first = await f.PublishAsync("První", RelatedFixture.Topic(12, 0));
        var second = await f.PublishAsync("Druhá", RelatedFixture.Topic(12, 0));
        await f.Index.ActiveIndexAsync();
        await f.Index.Host.SeedAsync(s => s.CreateSQLQuery("""UPDATE "PageNeighbor" SET "Similarity"=1""").ExecuteUpdateAsync());
        Assert.Single((await f.Index.As<DuplicatePageList>(f.Admin, new GetDuplicatePages(f.Index.SpaceId))).Items);
        await f.Index.EditAndPublishAsync(second.Id, "Nová publikace", RelatedFixture.Unrelated);
        Assert.Empty((await f.Index.As<DuplicatePageList>(f.Admin, new GetDuplicatePages(f.Index.SpaceId))).Items);

    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Dismissing_a_pair_is_idempotent_in_both_orders_and_cannot_target_another_space()
    {
        await using var f = await RelatedFixture.CreateAsync();
        var first = await f.PublishAsync("První", RelatedFixture.Topic(12, 0));
        var second = await f.PublishAsync("Druhá", RelatedFixture.Topic(12, 0));
        var outside = await f.PublishAsync("Jinde", RelatedFixture.Topic(12, 0), f.ExcludedSpaceId);
        await f.Index.ActiveIndexAsync();
        await f.Index.Host.SeedAsync(s => s.CreateSQLQuery("""UPDATE "PageNeighbor" SET "Similarity"=1""").ExecuteUpdateAsync());
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Index.As<bool>(f.Reader,
            new DismissDuplicatePages(f.Index.SpaceId, first.Id, second.Id)))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.Index.As<bool>(f.Admin,
            new DismissDuplicatePages(f.Index.SpaceId, first.Id, outside.Id)))).Status);
        Assert.True(await f.Index.As<bool>(f.Admin, new DismissDuplicatePages(f.Index.SpaceId, first.Id, second.Id)));
        Assert.True(await f.Index.As<bool>(f.Admin, new DismissDuplicatePages(f.Index.SpaceId, second.Id, first.Id)));
        Assert.Empty((await f.Index.As<DuplicatePageList>(f.Admin, new GetDuplicatePages(f.Index.SpaceId))).Items);
        Assert.Equal(1, await f.Index.CountAsync("""SELECT count(*) FROM "AiDuplicateDismissal" """));
    }
}
