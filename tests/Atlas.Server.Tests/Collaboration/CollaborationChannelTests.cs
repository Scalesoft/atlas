using Atlas.Server.Features.PageHistory.Commands;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>D3: the mcp channel refuses any unsaved live edits, the token owner's own included, for resets and for publishing.
/// Web and api keep 1.5.1: only other users' edits refuse a reset, publishing is unguarded. The channel is set on the test host.</summary>
public sealed class CollaborationChannelTests
{
    [PostgreSqlFact]
    public async Task Mcp_refuses_even_own_unsaved_live_edits()
    {
        await using var f = await Fixture.CreateAsync();
        var token = await TestApiTokens.SeedAsync(f.Host, f.Writer, "Agent");
        var page = await f.Pages.CreateAsync(new(f.SpaceId, null, "Mine", Fixture.Rich("Mine"), "Mine"), f.Writer, default);
        var opened = await f.Collaboration.OpenAsync(page.Id, f.Writer);
        await f.Collaboration.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), f.Writer.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 1 });

        f.Host.Channel.Current = new RequestChannel(ContentChannel.Mcp, token);
        var refused = await Assert.ThrowsAsync<ApiException>(() => f.Pages.SaveAsync(page.Id, new("Agent", Fixture.Rich("Agent"), "agent", 1), f.Writer, default));
        Assert.Equal(409, refused.Status);
        Assert.Equal("collaboration_unsaved", refused.Code);
        Assert.DoesNotContain(f.Writer.DisplayName, refused.Message);
        Assert.Equal(1L, await f.Scalar("SELECT count(*) FROM \"CollaborationUpdate\" WHERE \"PageId\"=:id", page.Id));

        // The api channel keeps 1.5.1: the owner's own unsaved edits are superseded by the save.
        f.Host.Channel.Current = new RequestChannel(ContentChannel.Api, token);
        Assert.Equal(2, (await f.Pages.SaveAsync(page.Id, new("Api", Fixture.Rich("Api"), "api", 1), f.Writer, default)).Version);
        Assert.Equal(0L, await f.Scalar("SELECT count(*) FROM \"CollaborationUpdate\" WHERE \"PageId\"=:id", page.Id));

        // A restore over mcp resets the generation too, so it refuses the owner's new unsaved edits as well.
        var again = await f.Collaboration.OpenAsync(page.Id, f.Writer);
        await f.Collaboration.AppendUpdateAsync(again.DocumentId, Guid.NewGuid(), f.Writer.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 2 });
        var first = await f.FirstRevisionAsync(page.Id);
        f.Host.Channel.Current = new RequestChannel(ContentChannel.Mcp, token);
        f.Host.Actor.Current = f.Writer;
        var restore = await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync<PageDto>(new RestorePage(page.Id, new RestorePageRequest(first, 2))));
        Assert.Equal("collaboration_unsaved", restore.Code);
        Assert.Equal(1L, await f.Scalar("SELECT count(*) FROM \"CollaborationUpdate\" WHERE \"PageId\"=:id", page.Id));
    }

    [PostgreSqlFact]
    public async Task Mcp_publication_is_refused_over_unsaved_live_edits()
    {
        await using var f = await Fixture.CreateAsync();
        var token = await TestApiTokens.SeedAsync(f.Host, f.Writer, "Agent");
        var page = await f.Pages.CreateAsync(new(f.SpaceId, null, "Shared", Fixture.Rich("Shared"), "Shared"), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        await f.Pages.SaveAsync(page.Id, new("Shared", Fixture.Rich("Changed"), "Changed", 1), f.Writer, default);
        var opened = await f.Collaboration.OpenAsync(page.Id, f.OtherWriter);
        await f.Collaboration.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), f.OtherWriter.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 1 });

        f.Host.Channel.Current = new RequestChannel(ContentChannel.Mcp, token);
        var refused = await Assert.ThrowsAsync<ApiException>(() => f.Pages.PublishAsync(page.Id, new(2), f.Writer, default));
        Assert.Equal(409, refused.Status);
        Assert.Equal("collaboration_unsaved", refused.Code);
        Assert.DoesNotContain(f.OtherWriter.DisplayName, refused.Message);
        Assert.Equal(1L, await f.Scalar("SELECT count(*) FROM \"PagePublication\" WHERE \"PageId\"=:id", page.Id));

        // Web publishing stays unguarded: it publishes the saved revision and leaves the live log alone.
        f.Host.Channel.Current = RequestChannel.Web;
        Assert.Equal(2, (await f.Pages.PublishAsync(page.Id, new(2), f.Writer, default)).PublishedVersion);
        Assert.Equal(1L, await f.Scalar("SELECT count(*) FROM \"CollaborationUpdate\" WHERE \"PageId\"=:id", page.Id));
        Assert.Equal(2L, await f.Scalar("SELECT count(*) FROM \"PagePublication\" WHERE \"PageId\"=:id AND \"Channel\"='web' AND \"ApiTokenId\" IS NULL", page.Id));
    }

    /// <summary>UX EP2-8: Publikovat změny outside editing asks to refuse the publication over unsaved live edits, the publisher's own included
    /// (a closed window's edits), instead of publishing the saved revision without them.</summary>
    [PostgreSqlFact]
    public async Task Web_publication_outside_editing_is_refused_over_unsaved_live_edits()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Pages.CreateAsync(new(f.SpaceId, null, "Catalog", Fixture.Rich("Catalog"), "Catalog"), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        await f.Pages.SaveAsync(page.Id, new("Catalog", Fixture.Rich("Draft"), "Draft", 1), f.Writer, default);
        var opened = await f.Collaboration.OpenAsync(page.Id, f.Writer);
        await f.Collaboration.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), f.Writer.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 1 });

        f.Host.Channel.Current = RequestChannel.Web;
        var refused = await Assert.ThrowsAsync<ApiException>(() => f.Pages.PublishAsync(page.Id, new(2, null, RefuseUnsavedLiveEdits: true), f.Writer, default));
        Assert.Equal((409, "collaboration_unsaved"), (refused.Status, refused.Code));
        Assert.Contains("Uloženo", refused.Message);
        Assert.Equal(1L, await f.Scalar("SELECT count(*) FROM \"PagePublication\" WHERE \"PageId\"=:id", page.Id));
        Assert.Equal(1L, await f.Scalar("SELECT count(*) FROM \"CollaborationUpdate\" WHERE \"PageId\"=:id", page.Id));
    }

    /// <summary>S5: the assistant (no token) refuses its own user's unsaved live edits like mcp, for a save and for publishing; once they
    /// are gone its save records the channel assistant.</summary>
    [PostgreSqlFact]
    public async Task Assistant_refuses_own_unsaved_live_edits_and_records_its_channel()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Pages.CreateAsync(new(f.SpaceId, null, "Mine", Fixture.Rich("Mine"), "Mine"), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        await f.Pages.SaveAsync(page.Id, new("Mine", Fixture.Rich("Changed"), "Changed", 1), f.Writer, default);
        var opened = await f.Collaboration.OpenAsync(page.Id, f.Writer);
        await f.Collaboration.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), f.Writer.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 1 });

        f.Host.Channel.Current = RequestChannel.Assistant;
        Assert.Equal("collaboration_unsaved", (await Assert.ThrowsAsync<ApiException>(() =>
            f.Pages.SaveAsync(page.Id, new("Assistant", Fixture.Rich("Assistant"), "assistant", 2), f.Writer, default))).Code);
        Assert.Equal("collaboration_unsaved", (await Assert.ThrowsAsync<ApiException>(() => f.Pages.PublishAsync(page.Id, new(2), f.Writer, default))).Code);
        Assert.Equal(1L, await f.Scalar("SELECT count(*) FROM \"CollaborationUpdate\" WHERE \"PageId\"=:id", page.Id));

        f.Host.Channel.Current = RequestChannel.Web;
        await f.Pages.SaveAsync(page.Id, new("Mine", Fixture.Rich("Saved"), "Saved", 2), f.Writer, default);
        f.Host.Channel.Current = RequestChannel.Assistant;
        Assert.Equal(4, (await f.Pages.SaveAsync(page.Id, new("Assistant", Fixture.Rich("Assistant"), "assistant", 3), f.Writer, default)).Version);
        Assert.Equal(1L, await f.Scalar("SELECT count(*) FROM \"PageRevision\" WHERE \"PageId\"=:id AND \"Version\"=4 AND \"Channel\"='assistant' AND \"ApiTokenId\" IS NULL", page.Id));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(AtlasTestHost host)
        {
            Host = host;
            Pages = new TestPages(host);
            Collaboration = new TestCollaboration(host);
        }

        public AtlasTestHost Host { get; }
        public TestPages Pages { get; }
        public TestCollaboration Collaboration { get; }
        public UserAccount Writer { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("writer"), DisplayName = "Writer Person" };
        public UserAccount OtherWriter { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("other"), DisplayName = "Other Writer" };
        public Guid SpaceId { get; private set; }

        public static string Rich(string text) => "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(await AtlasTestHost.CreateAsync("atlas_collab_channel_"));
            await f.Host.SeedAsync(async session =>
            {
                await session.SaveAsync(f.Writer);
                await session.SaveAsync(f.OtherWriter);
            });
            f.SpaceId = await f.Host.SeedSpaceAsync("Channels", (f.Writer, SpaceRole.Writer), (f.OtherWriter, SpaceRole.Writer));
            return f;
        }

        public async Task<Guid> FirstRevisionAsync(Guid pageId)
        {
            using var session = Host.OpenSession();
            return await session.QueryOver<PageRevision>().Where(x => x.PageId == pageId && x.Version == 1).Select(x => x.Id).SingleOrDefaultAsync<Guid>();
        }

        public async Task<long> Scalar(string sql, Guid id)
        {
            using var session = Host.OpenSession();
            return Convert.ToInt64(await session.CreateSQLQuery(sql).SetParameter("id", id).UniqueResultAsync());
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
