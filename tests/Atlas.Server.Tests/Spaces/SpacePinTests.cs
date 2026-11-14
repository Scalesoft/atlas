using Atlas.Server.Features.Spaces.Commands;
using Atlas.Server.Features.Spaces.Contracts;
using Atlas.Server.Features.Spaces.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Spaces;

public sealed class SpacePinTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";

    [PostgreSqlFact]
    public async Task Own_choice_overrides_None_and_Default_never_Required_and_outlives_policy_changes_and_lost_access()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_space_pins_");
        var reader = await host.SeedUserAsync("reader");
        var outsider = await host.SeedUserAsync("outsider");
        var spaceId = await host.SeedSpaceAsync("Handbook", (reader, SpaceRole.Reader));
        host.Actor.Current = reader;
        Assert.False(await PinnedAsync(host));
        // Two tabs pinning at once leave one choice (per-account lock, current-row index, EXCLUDE).
        Assert.All(await Task.WhenAll(PinAsync(host, spaceId, true), PinAsync(host, spaceId, true)), state => Assert.True(state.Pinned));
        await host.ExecuteAsync("UPDATE \"Space\" SET \"PinPolicy\"='Required' WHERE \"Id\"=:id", spaceId);
        var refused = await Assert.ThrowsAsync<ApiException>(() => PinAsync(host, spaceId, false));
        Assert.Equal((409, "space_pin_required"), (refused.Status, refused.Code));
        await host.ExecuteAsync("UPDATE \"Space\" SET \"PinPolicy\"='None' WHERE \"Id\"=:id", spaceId);
        Assert.True(await PinnedAsync(host));
        await host.ExecuteAsync("UPDATE \"Space\" SET \"PinPolicy\"='Default' WHERE \"Id\"=:id", spaceId);
        Assert.False((await PinAsync(host, spaceId, false)).Pinned);
        Assert.False(await PinnedAsync(host));

        // Without access the space is 404 and absent from the list; the explicit unpin applies again once access is back.
        await host.ExecuteAsync("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL", reader.Id);
        Assert.Empty((await host.InvokeAsync<AccessibleSpaces>(new GetAccessibleSpaces())).Items);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => PinAsync(host, spaceId, true))).Status);
        await host.ExecuteAsync("""
            INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","Role","ActiveFrom")
            SELECT gen_random_uuid(),"SpaceId","UserId",1,clock_timestamp() FROM "SpaceGrant" WHERE "UserId"=:id
            """, reader.Id);
        Assert.False(await PinnedAsync(host));
        using var session = host.OpenSession();
        var history = await session.QueryOver<SpacePin>().Where(x => x.UserId == reader.Id && x.SpaceId == spaceId).OrderBy(x => x.ActiveFrom).Asc.ListAsync();
        Assert.Equal([true, false], history.Select(x => x.Pinned));
        Assert.Equal(history[0].ActiveTo, history[1].ActiveFrom);
        Assert.Null(history[1].ActiveTo);

        host.Actor.Current = outsider;
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => PinAsync(host, spaceId, true))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => PinAsync(host, Guid.NewGuid(), true))).Status);
    }

    [PostgreSqlFact]
    public async Task Page_count_and_last_change_reveal_neither_a_draft_nor_a_restricted_page_to_a_reader()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_space_pages_"); // the wiki administrator's list holds the first 200 spaces of the whole installation by name: on the shared host this test's spaces can fall beyond it
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var writer = await host.SeedUserAsync("writer");
        var reader = await host.SeedUserAsync("reader");
        var spaceId = await host.SeedSpaceAsync("Handbook", (writer, SpaceRole.Writer), (reader, SpaceRole.Reader));
        await host.SeedSpaceAsync("Empty", (reader, SpaceRole.Reader));
        var pages = new TestPages(host);
        var published = await pages.CreateAsync(new(spaceId, null, "Published", Rich, "p"), writer, default);
        await pages.PublishAsync(published.Id, new(1), writer, default);
        var restricted = await pages.CreateAsync(new(spaceId, null, "Restricted", Rich, "r"), writer, default);
        await pages.PublishAsync(restricted.Id, new(1), writer, default);
        await host.ExecuteAsync("UPDATE \"Page\" SET \"OwnAccess\"=true WHERE \"Id\"=:id", restricted.Id);
        await pages.SaveAsync(published.Id, new("Edited", Rich, "e", 1), writer, default);
        var draft = await pages.CreateAsync(new(spaceId, null, "Draft", Rich, "d"), writer, default);

        using var session = host.OpenSession();
        async Task<DateTime> RevisionAsync(Guid pageId, int version) => await session.QueryOver<PageRevision>()
            .Where(x => x.PageId == pageId && x.Version == version).Select(x => x.ActiveFrom).SingleOrDefaultAsync<DateTime>();
        var publishedAt = await RevisionAsync(published.Id, 1);
        var draftAt = await RevisionAsync(draft.Id, 1);

        host.Actor.Current = reader;
        var forReader = (await host.InvokeAsync<AccessibleSpaces>(new GetAccessibleSpaces())).Items;
        Assert.Equal((0, (DateTime?)null), (forReader[0].PageCount, forReader[0].LastChangedAt));
        Assert.Equal((1, (DateTime?)publishedAt), (forReader[1].PageCount, forReader[1].LastChangedAt));
        Assert.Equal(DateTimeKind.Utc, forReader[1].LastChangedAt!.Value.Kind);
        host.Actor.Current = writer;
        var forWriter = Assert.Single((await host.InvokeAsync<AccessibleSpaces>(new GetAccessibleSpaces())).Items);
        Assert.Equal((2, (DateTime?)draftAt), (forWriter.PageCount, forWriter.LastChangedAt));
        host.Actor.Current = admin;
        Assert.Equal(3, (await host.InvokeAsync<AccessibleSpaces>(new GetAccessibleSpaces())).Items.Single(x => x.Id == spaceId).PageCount);
    }

    private static Task<SpacePinState> PinAsync(AtlasTestHost host, Guid spaceId, bool pinned)
    {
        return host.InvokeAsync<SpacePinState>(new SetSpacePin(spaceId, pinned));
    }

    private static async Task<bool> PinnedAsync(AtlasTestHost host)
    {
        return Assert.Single((await host.InvokeAsync<AccessibleSpaces>(new GetAccessibleSpaces())).Items).Pinned;
    }
}
