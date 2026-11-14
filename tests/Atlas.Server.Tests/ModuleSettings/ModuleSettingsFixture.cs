using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Features.ModuleSettings.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;
using Atlas.Server.Features.Auth.OpenIdConnect;

namespace Atlas.Server.Tests.ModuleSettings;

/// <summary>The real AddModuleSettings registrations, with only the two host-level singletons stubbed:
/// an ephemeral protector and a runtime configuration that has no stored version yet.</summary>
internal sealed class ModuleSettingsFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;

    private ModuleSettingsFixture(AtlasTestHost host, UserAccount admin, UserAccount member)
    {
        m_host = host;
        Admin = admin;
        Member = member;
    }

    public AtlasTestHost Host => m_host;
    public UserAccount Admin { get; }
    public UserAccount Member { get; }

    // ModuleOnboarding references Workspace(1); the setup handler tests seed onboarding rows against it. One row per host.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AtlasTestHost, Lazy<Task>> s_workspace = new();

    /// <param name="configure">Services of the test's own (a fake discovery client): the test then gets a host of its own.</param>
    /// <param name="privateBecause">Why the test cannot share the "module-settings" host: it saves module settings, which are rows of the
    /// whole installation (name the table); it then gets a host of its own.</param>
    public static async Task<ModuleSettingsFixture> CreateAsync(Action<IServiceCollection>? configure = null, string? privateBecause = null)
    {
        var host = await AtlasTestHost.CreateAsync("atlas_modules_", sharedAs: configure is null && privateBecause is null ? "module-settings" : null, configure: services =>
        {
            // The catalog, the access ladder and the discovery client come from the production registration,
            // so a descriptor or ordering change here fails the test instead of quietly diverging from it.
            services.AddModuleSettings(new ConfigurationBuilder().Build());
            services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
            services.AddSingleton(new OidcRuntimeConfiguration(null, new AtlasOidcOptions { BuiltinEnabled = true }));
            configure?.Invoke(services);
        });
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var member = await host.SeedUserAsync("member");
        await s_workspace.GetValue(host, shared => new Lazy<Task>(() =>
            shared.SeedAsync(session => session.SaveAsync(new Workspace { Id = 1, Name = "Test", CreatedAt = DateTime.UtcNow })))).Value;
        return new ModuleSettingsFixture(host, admin, member);
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

    public static async Task Denied(int status, Func<Task> action)
    {
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);
    }

    public ValueTask DisposeAsync() => m_host.DisposeAsync();
}
