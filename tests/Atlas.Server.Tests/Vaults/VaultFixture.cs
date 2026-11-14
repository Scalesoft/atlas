using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Server.Features.Auth;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.ContentExport;
using Atlas.Server.Features.Security;
using Atlas.Server.Features.Security.Networks;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Queries;
using Atlas.Server.Features.Vaults.Release;
using Atlas.Server.Features.Vaults.Runtime;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.Vaults;

/// <summary>
/// Real PostgreSQL, Wolverine and vault runtime with the background refresh off: tests drive refreshes
/// themselves. A local folder under the temp directory is the vault location. Tests of one configuration share a host
/// (sharedAs): each works in its own space, folder, location and vaults, and refreshes only its own (<see cref="RefreshAsync"/>).
/// </summary>
internal sealed class VaultFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;
    private readonly bool m_shared;

    private VaultFixture(AtlasTestHost host, bool shared, UserAccount admin, UserAccount spaceAdmin, Guid spaceId, string folder)
    {
        m_host = host;
        m_shared = shared;
        Admin = admin;
        SpaceAdmin = spaceAdmin;
        SpaceId = spaceId;
        Folder = folder;
    }

    public AtlasTestHost Host => m_host;
    public UserAccount Admin { get; }
    public UserAccount SpaceAdmin { get; }
    public Guid SpaceId { get; }
    public string Folder { get; }
    public VaultSnapshots Snapshots => m_host.Services.GetRequiredService<VaultSnapshots>();
    public VaultKeyring Keyring => m_host.Services.GetRequiredService<VaultKeyring>();
    /// <summary>The live events of a host with a <c>release</c> policy; shared by its tests, so a test reads them by its own ids.</summary>
    public RecordingSecurityEvents Events => m_host.Services.GetRequiredService<RecordingSecurityEvents>();

    /// <param name="release">The test's release policy switchboard (with recorded live events); null keeps the real policy.</param>
    /// <param name="settings">Vault settings; each distinct set is a host of its own, shared by the tests that ask for it.</param>
    /// <param name="configure">Services that replace the host's for every test on it (a fake that fails on purpose): a private host.</param>
    /// <param name="isolated">A private host and database, for a test that changes the schema or counts what the whole host does.</param>
    public static Task<VaultFixture> CreateAsync(FakeReleasePolicy? release = null, IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configure = null, bool isolated = false)
    {
        // Set here, not in the async body: an async method restores the caller's AsyncLocal values when it returns.
        release?.Use();
        return CreateCoreAsync(release is not null, settings ?? new Dictionary<string, string?>(), configure, isolated || configure is not null);
    }

    private static async Task<VaultFixture> CreateCoreAsync(bool release, IReadOnlyDictionary<string, string?> settings,
        Action<IServiceCollection>? configure, bool isolated)
    {
        var folder = Path.Combine(Path.GetTempPath(), "atlas-vault-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Vaults:RefreshSeconds"] = "0" })
            .AddInMemoryCollection(settings)
            .Build();
        void Configure(IServiceCollection services)
        {
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddVaults(configuration);
            services.AddSingleton(new AtlasOidcOptions());
            services.AddSingleton(new TrustedProxyEnvironment([], false));
            services.AddAuth();
            services.AddSecurityCenter();
            services.Replace(ServiceDescriptor.Scoped<ISecurityChangeGuard, PermitSecurityChanges>());
            // Exports (a reader's copy of a page with references) go to the host's own temporary content root.
            services.AddSingleton<ContentExportFiles>();
            if (release)
            {
                services.AddScoped<IReleasePolicy>(_ => FakeReleasePolicy.Current);
                services.AddSingleton<RecordingSecurityEvents>();
                services.AddSingleton<ISecurityEvents>(provider => provider.GetRequiredService<RecordingSecurityEvents>());
            }
            configure?.Invoke(services);
        }
        var name = "vaults" + (release ? "-release" : "") + string.Concat(settings.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"|{s.Key}={s.Value}"));
        var host = isolated
            ? await AtlasTestHost.CreateIsolatedAsync("atlas_vaults_", Configure)
            : await AtlasTestHost.CreateAsync("atlas_vaults_", Configure, sharedAs: name);
        var admin = await host.SeedUserAsync("wiki-admin", isWikiAdmin: true);
        var spaceAdmin = await host.SeedUserAsync("space-admin");
        var spaceId = await host.SeedSpaceAsync("Provoz", (spaceAdmin, SpaceRole.Admin));
        return new VaultFixture(host, !isolated, admin, spaceAdmin, spaceId, folder);
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

    /// <summary>Runs a file operation as the actor in its own DI scope, as an endpoint would.</summary>
    public async Task<T> Operate<T>(UserAccount actor, Func<VaultOperations, Task<T>> operation)
    {
        m_host.Actor.Current = actor;
        using var scope = m_host.Services.CreateScope();
        return await operation(scope.ServiceProvider.GetRequiredService<VaultOperations>());
    }

    public async Task Operate(UserAccount actor, Func<VaultOperations, Task> operation)
    {
        m_host.Actor.Current = actor;
        using var scope = m_host.Services.CreateScope();
        await operation(scope.ServiceProvider.GetRequiredService<VaultOperations>());
    }

    /// <summary>A location name of this test: names are unique in the installation, which a shared host shares with other tests.</summary>
    public string Name(string name) => m_shared ? TestLogin.For(name) : name;

    public Task<VaultLocationView> LocalLocationAsync(string? name = null) =>
        As<VaultLocationView>(Admin, new SaveVaultLocation(null, new SaveVaultLocationRequest(name ?? Name("Místní trezory"), "local", Folder)));

    /// <summary>One pass of the background refresh (VaultRefreshService.RefreshAllAsync) over the vaults of the fixture space only.</summary>
    public async Task RefreshAsync()
    {
        var targets = await m_host.InvokeAsync<IReadOnlyList<VaultTarget>>(new ListRefreshableVaults());
        IList<Guid> own;
        using (var session = m_host.OpenSession())
        {
            own = await session.CreateSQLQuery("SELECT \"Id\" FROM \"Vault\" WHERE \"SpaceId\" = :space").SetParameter("space", SpaceId).ListAsync<Guid>();
        }
        foreach (var target in targets.Where(target => own.Contains(target.VaultId)))
        {
            await m_host.Services.GetRequiredService<VaultRefreshService>().RefreshAsync(target, false, CancellationToken.None);
        }
    }

    /// <summary>Copies a KeePass fixture into the location with a fresh modification time.</summary>
    public void PutFile(string fileName, string fixture) => File.WriteAllBytes(Path.Combine(Folder, fileName), KdbxTestFiles.Read(fixture));

    public async Task<VaultView> AttachAsync(Guid locationId, string fileName, string fixture, string? password = null, string name = "Provozní hesla")
    {
        PutFile(fileName, fixture);
        return await Operate(SpaceAdmin, operations => operations.AttachAsync(
            new AttachVaultRequest(SpaceId, locationId, name, fileName, password ?? KdbxTestFiles.Password(fixture)), CancellationToken.None));
    }

    public async ValueTask DisposeAsync()
    {
        await m_host.DisposeAsync();
        try
        {
            Directory.Delete(Folder, true);
        }
        catch (IOException)
        {
            // A scratch folder left behind in the temp directory is harmless.
        }
    }
}
