using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.PersonalLibrary;

internal sealed class PersonalLibraryFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;

    private PersonalLibraryFixture(AtlasTestHost host)
    {
        m_host = host;
        Pages = new TestPages(host);
    }

    public AtlasTestHost Host => m_host;
    public TestPages Pages { get; }
    public UserAccount Admin { get; private set; } = null!;
    public UserAccount Writer { get; private set; } = null!;
    public UserAccount OtherWriter { get; private set; } = null!;
    public UserAccount Reader { get; private set; } = null!;
    public Guid SpaceId { get; private set; }

    /// <param name="isolated">A host and database of its own, for a test that runs a sweep over whole tables (say why at the call).</param>
    public static async Task<PersonalLibraryFixture> CreateAsync(bool isolated = false)
    {
        var host = isolated ? await AtlasTestHost.CreateIsolatedAsync("atlas_library_") : await AtlasTestHost.CreateAsync("atlas_library_");
        var fixture = new PersonalLibraryFixture(host);
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

    public Task<PageDto> Create(string title, Guid? parentId = null, UserAccount? owner = null)
    {
        return Pages.CreateAsync(new CreatePageRequest(SpaceId, parentId, title, Rich(title), title), owner ?? Writer, default);
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
