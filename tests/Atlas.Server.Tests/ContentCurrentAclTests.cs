using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.Spaces.Contracts;
using Atlas.Server.Features.Spaces.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class ContentCurrentAclTests
{
    [PostgreSqlFact]
    public async Task Space_and_page_lists_revalidate_a_cached_request_actor_before_returning_anything()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Execute("UPDATE \"UserAccount\" SET \"IsWikiAdmin\"=false WHERE \"Id\"=:id", f.Admin.Id);
        // The actor instance the test passes is the one middleware cached: it still claims wiki admin after the demotion,
        // so both handlers must re-read the account under their own locks before answering.
        Assert.Empty((await f.As<AccessibleSpaces>(f.Admin, new GetAccessibleSpaces())).Items);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<SpacePages>(f.Admin, new GetSpacePages(f.SpaceId)))).Status);
    }

    [PostgreSqlFact]
    public async Task Stale_global_admin_cannot_read_foreign_private_or_create_after_demotion()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Writer private", Fixture.Rich("x"), "x")));
        await f.Execute("UPDATE \"UserAccount\" SET \"IsWikiAdmin\"=false WHERE \"Id\"=:id", f.Admin.Id);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<PageDto>(f.Admin, new GetPage(page.Id)))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<PageDto>(f.Admin, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Forbidden", Fixture.Rich("x"), "x"))))).Status);
    }

    [PostgreSqlFact]
    public async Task Changed_stamp_rejects_page_read_and_create_even_with_unchanged_space_grant()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Private", Fixture.Rich("x"), "x")));
        await f.Execute("UPDATE \"UserAccount\" SET \"SecurityStamp\"='revoked-proof' WHERE \"Id\"=:id", f.Writer.Id);
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => f.As<PageDto>(f.Writer, new GetPage(page.Id)))).Status);
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Forbidden", Fixture.Rich("x"), "x"))))).Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AtlasTestHost m_host;

        private Fixture(AtlasTestHost host)
        {
            m_host = host;
        }

        public AtlasDatabase Database => m_host.Database;
        public UserAccount Admin { get; private set; } = null!;
        public UserAccount Writer { get; private set; } = null!;
        public Guid SpaceId { get; private set; }

        public static async Task<Fixture> CreateAsync()
        {
            var host = await AtlasTestHost.CreateAsync("atlas_content_acl_");
            var fixture = new Fixture(host);
            fixture.Admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
            fixture.Writer = await host.SeedUserAsync("writer");
            fixture.SpaceId = await host.SeedSpaceAsync("History", (fixture.Writer, SpaceRole.Writer));
            return fixture;
        }

        public static string Rich(string text)
        {
            return "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";
        }

        public Task<T> As<T>(UserAccount actor, object message)
        {
            m_host.Actor.Current = actor;
            return m_host.InvokeAsync<T>(message);
        }

        public Task Execute(string sql, Guid id)
        {
            return m_host.ExecuteAsync(sql, id);
        }

        public ValueTask DisposeAsync()
        {
            return m_host.DisposeAsync();
        }
    }
}
