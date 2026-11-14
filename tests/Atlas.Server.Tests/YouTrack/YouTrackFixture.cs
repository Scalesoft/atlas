using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.YouTrack;
using Atlas.Server.Tests.Platform;
using Atlas.YouTrack;

namespace Atlas.Server.Tests.YouTrack;

internal sealed class YouTrackFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;

    private YouTrackFixture(AtlasTestHost host)
    {
        m_host = host;
    }

    public AtlasTestHost Host => m_host;

    public Guid SpaceId { get; } = Guid.NewGuid();

    public UserAccount Admin { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("admin"), DisplayName = "Admin", IsWikiAdmin = true };

    public UserAccount Reader { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("reader"), DisplayName = "Reader" };

    /// <param name="configuredToken">The server configuration's own YouTrack:Token (the token of a deployment without a stored
    /// configuration row); empty when null.</param>
    /// <param name="savesSettings">The test saves the installation's one current YouTrackConfiguration row or lists every space: a host
    /// and database of its own. Without it (and without a token) the tests share one host on which nobody saves settings.</param>
    public static async Task<YouTrackFixture> CreateAsync(string? configuredToken = null, bool savesSettings = false)
    {
        var settings = new Dictionary<string, string?> { ["YouTrack:Enabled"] = "true" };
        if (configuredToken is not null)
        {
            settings["YouTrack:Token"] = configuredToken;
        }
        void Configure(IServiceCollection services)
        {
            services.AddDataProtection().SetApplicationName("ScaleAtlas");
            services.AddAtlasYouTrack(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
            services.AddYouTrack();
        }
        var host = savesSettings || configuredToken is not null
            // Own database: the installation's YouTrack configuration is written, or read with none stored (YouTrack:Token).
            ? await AtlasTestHost.CreateIsolatedAsync("atlas_youtrack_", Configure)
            : await AtlasTestHost.CreateAsync("atlas_youtrack_", Configure, sharedAs: "youtrack");
        var fixture = new YouTrackFixture(host);
        using var session = host.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(fixture.Admin);
        await session.SaveAsync(fixture.Reader);
        await session.SaveAsync(new Space { Id = fixture.SpaceId, Name = "Engineering" });
        await session.FlushAsync();
        await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = fixture.SpaceId, UserId = fixture.Reader.Id, Role = 1 });
        await transaction.CommitAsync();
        return fixture;
    }

    public Task<T> As<T>(UserAccount actor, object message)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync<T>(message);
    }

    public ValueTask DisposeAsync() => m_host.DisposeAsync();
}
