using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Administration;
using Atlas.Server.Tests.Platform;
using Xunit;
using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth;

namespace Atlas.Server.Tests.Administration;

internal sealed class AdministrationFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;

    private AdministrationFixture(AtlasTestHost host)
    {
        m_host = host;
    }

    public AtlasTestHost Host => m_host;
    public UserAccount Admin { get; private set; } = null!;
    public UserAccount Member { get; private set; } = null!;
    public Guid SpaceId { get; private set; }

    /// <param name="privateBecause">Why the test cannot share the "administration" host (a row or a count of the whole installation); the
    /// test then gets a host of its own.</param>
    public static async Task<AdministrationFixture> CreateAsync(string? privateBecause = null)
    {
        var host = await AtlasTestHost.CreateAsync("atlas_admin_", sharedAs: privateBecause is null ? "administration" : null, configure: services =>
        {
            services.AddAdministration();
            // The credential audit function lives in Auth; the handlers under test call it directly.
            services.AddScoped<CredentialAudit>();
            services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
            services.AddSingleton(new BuiltinAuthenticationOptions(true));
            services.AddSingleton(BuiltinPasswordPolicy.Production);
            services.AddSingleton<BuiltinLoginThrottle>();
        });
        var fixture = new AdministrationFixture(host)
        {
            Admin = await host.SeedUserAsync("admin", isWikiAdmin: true),
            Member = await host.SeedUserAsync("member")
        };
        fixture.SpaceId = await host.SeedSpaceAsync("Docs", (fixture.Admin, SpaceRole.Admin));
        return fixture;
    }

    public Task<T> As<T>(UserAccount actor, object message)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync<T>(message);
    }

    /// <summary>Anonymous messages (activation) pass a null actor.</summary>
    public Task As(UserAccount? actor, object message)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync(message);
    }

    /// <summary>Re-reads an account so a test can act with the snapshot the database holds now, not the one it seeded with.</summary>
    public async Task<UserAccount> Account(Guid id)
    {
        using var session = m_host.OpenSession();
        return await session.GetAsync<UserAccount>(id);
    }

    public static async Task Denied(int status, Func<Task> action)
    {
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);
    }

    /// <summary>The wire contract freezes the wording as tightly as the status code, so the ladder messages are asserted verbatim.</summary>
    public static async Task Denied(int status, string message, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(status, error.Status);
        Assert.Equal(message, error.Message);
    }

    public ValueTask DisposeAsync() => m_host.DisposeAsync();
}
