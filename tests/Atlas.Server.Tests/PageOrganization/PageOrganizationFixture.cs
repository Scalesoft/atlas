using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.PageOrganization;

internal sealed class PageOrganizationFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;

    private PageOrganizationFixture(AtlasTestHost host)
    {
        m_host = host;
    }

    public AtlasTestHost Host => m_host;
    public TestPages Pages => new(m_host);
    public TestPageHistory History => new(m_host);
    public TestCollaboration Collaboration => new(m_host);
    public UserAccount Admin { get; private set; } = null!;
    public UserAccount Writer { get; private set; } = null!;
    public UserAccount OtherWriter { get; private set; } = null!;
    public UserAccount Reader { get; private set; } = null!;
    public Guid SpaceId { get; private set; }

    public static async Task<PageOrganizationFixture> CreateAsync()
    {
        var host = await AtlasTestHost.CreateAsync("atlas_organization_");
        var fixture = new PageOrganizationFixture(host);
        fixture.Admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        fixture.Writer = await host.SeedUserAsync("writer");
        fixture.OtherWriter = await host.SeedUserAsync("other");
        fixture.Reader = await host.SeedUserAsync("reader");
        fixture.SpaceId = await host.SeedSpaceAsync("History",
            (fixture.Writer, SpaceRole.Writer), (fixture.OtherWriter, SpaceRole.Writer), (fixture.Reader, SpaceRole.Reader));
        return fixture;
    }

    public static string Rich(string text)
    {
        return "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";
    }

    public async Task<Guid> CreateAsync(string title, Guid? parentId = null, UserAccount? owner = null)
    {
        var actor = owner ?? Writer;
        var page = await As<PageDto>(actor, new CreatePage(new CreatePageRequest(SpaceId, parentId, title, Rich(title), title)));
        return page.Id;
    }

    public async Task PublishAsync(Guid id, UserAccount? actor = null)
    {
        var page = await As<PageDto>(actor ?? Writer, new GetPage(id));
        await As<PageDto>(actor ?? Writer, new PublishPage(id, new PublishPageRequest(page.Version)));
    }

    public Task<T> As<T>(UserAccount actor, object message)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync<T>(message);
    }

    public Task As(UserAccount actor, object message)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync(message);
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
