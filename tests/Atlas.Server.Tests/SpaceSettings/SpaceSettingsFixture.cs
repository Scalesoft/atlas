using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.SpaceSettings;

internal sealed class SpaceSettingsFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;

    private SpaceSettingsFixture(AtlasTestHost host, UserAccount admin, UserAccount spaceAdmin, Guid spaceId)
    {
        m_host = host;
        Admin = admin;
        SpaceAdmin = spaceAdmin;
        SpaceId = spaceId;
    }

    public AtlasTestHost Host => m_host;
    public Guid SpaceId { get; }
    public UserAccount Admin { get; }
    public UserAccount SpaceAdmin { get; }

    public static async Task<SpaceSettingsFixture> CreateAsync()
    {
        var host = await AtlasTestHost.CreateAsync("atlas_space_settings_");
        var admin = await host.SeedUserAsync("global", isWikiAdmin: true);
        var spaceAdmin = await host.SeedUserAsync("owner");
        var spaceId = await host.SeedSpaceAsync("Original", (spaceAdmin, SpaceRole.Admin));
        return new SpaceSettingsFixture(host, admin, spaceAdmin, spaceId);
    }

    public Task<T> As<T>(UserAccount actor, object message)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync<T>(message);
    }

    public Task Execute(string sql, Guid id) => m_host.ExecuteAsync(sql, id);

    public ValueTask DisposeAsync() => m_host.DisposeAsync();
}
