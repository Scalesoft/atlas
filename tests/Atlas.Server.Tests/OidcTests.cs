using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NHibernate.Linq;
using Npgsql;
using OpenIddict.Abstractions;
using Xunit;
using Atlas.Server.Features.Auth;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Platform;
using Wolverine;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Auth.OpenIdConnect.Commands;
using Atlas.Server.Features.Auth.OpenIdConnect.Domain;
using Atlas.Server.Features.Auth.OpenIdConnect.Queries;
using Atlas.Server.Features.Security;
using Atlas.Server.Features.Hub;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Features.Administration.Domain;
using Microsoft.AspNetCore.DataProtection;

namespace Atlas.Server.Tests;

public sealed class OidcTests
{
    [SearchPostgresFact]
    public async Task First_hub_login_synchronizes_verified_membership_before_session_and_does_not_persist_claim_admin()
    {
        await using var fixture = await OidcFixture.Create(mapped: true);
        var group = await fixture.EnableHubAsync();
        var response = await fixture.Complete(await fixture.Begin());
        Assert.Equal("/page/test", response.Headers.Location!.OriginalString);
        using (var session = fixture.Database.OpenSession())
        {
            var user = Assert.Single(await session.Query<UserAccount>().Where(x => x.Username != "admin").ToListAsync());
            Assert.False(user.IsWikiAdmin);
            Assert.Single(await session.Query<GroupMembership>().Where(x => x.UserId == user.Id && x.GroupId == group).ToListAsync());
        }
        fixture.Provider.HubMember = false;
        await fixture.Complete(await fixture.Begin());
        using var after = fixture.Database.OpenSession();
        Assert.Empty(await after.Query<GroupMembership>().Where(x => x.GroupId == group).ToListAsync());
    }

    [SearchPostgresFact]
    public async Task Hub_link_rejects_a_second_active_subject_for_the_same_local_user()
    {
        await using var fixture = await OidcFixture.Create();
        await fixture.EnableHubAsync();
        var user = await fixture.Invoke<UserAccount>(new ResolveExternalIdentity("https://idp.test/", true, "first", "First", null, null));
        var conflict = await Assert.ThrowsAsync<ApiException>(() => fixture.Invoke<UserAccount>(new ResolveExternalIdentity("https://idp.test/", true, "second", "Second", user.Id, user.SecurityStamp)));
        Assert.Equal(409, conflict.Status);
        using var session = fixture.Database.OpenSession();
        Assert.Single(await session.Query<ExternalIdentity>().Where(x => x.UserId == user.Id && x.ActiveTo == null).ToListAsync());
    }

    [SearchPostgresFact]
    public async Task Verified_group_array_can_grant_administrator()
    {
        await using var fixture = await OidcFixture.Create(mapped: true, claimType: "groups");
        await fixture.Complete(await fixture.Begin());
        using var session = fixture.Database.OpenSession();
        var external = Assert.Single(await session.Query<UserAccount>().Where(x => x.Username != "admin").ToListAsync());
        Assert.True(external.IsWikiAdmin);
        Assert.Equal("https://idp.test/", external.OidcAdminIssuer);
    }

    [SearchPostgresFact]
    public async Task Mapping_preserves_manual_admin_and_other_issuer_grants_and_cannot_bypass_signup_or_activation()
    {
        await using var fixture = await OidcFixture.Create();
        var auto = await fixture.Invoke<UserAccount>(new ResolveExternalIdentity("https://a.test", true, "auto", "Auto", null, null, true));
        Assert.True(auto.IsWikiAdmin);
        var other = await fixture.Invoke<UserAccount>(new ResolveExternalIdentity("https://b.test", true, "other", "Auto", auto.Id, auto.SecurityStamp, false));
        Assert.True(other.IsWikiAdmin);
        Assert.Equal("https://a.test", other.OidcAdminIssuer);
        var manual = await fixture.Invoke<UserAccount>(new ResolveExternalIdentity("https://a.test", true, "manual", "Manual", fixture.Admin.Id, fixture.Admin.SecurityStamp, true));
        Assert.True(manual.IsWikiAdmin);
        Assert.Null(manual.OidcAdminIssuer);
        manual = await fixture.Invoke<UserAccount>(new ResolveExternalIdentity("https://a.test", true, "manual", "Manual", null, null, false));
        Assert.True(manual.IsWikiAdmin);
        await Assert.ThrowsAsync<ApiException>(() => fixture.Invoke<UserAccount>(new ResolveExternalIdentity("https://a.test", false, "unknown", "Unknown", null, null, true)));
        using (var session = fixture.Database.OpenSession())
        {
            using var tx = session.BeginTransaction();
            var deactivated = (await session.GetAsync<UserAccount>(auto.Id))!;
            deactivated.IsActive = false;
            await AccountHistory.RecordAsync(session, deactivated, null, CancellationToken.None);
            await tx.CommitAsync();
        }
        await Assert.ThrowsAsync<ApiException>(() => fixture.Invoke<UserAccount>(new ResolveExternalIdentity("https://a.test", true, "auto", "Auto", null, null, true)));
    }

    [SearchPostgresFact]
    public async Task Mapping_test_rejects_a_new_session_for_the_same_local_administrator()
    {
        await using var fixture = await OidcFixture.Create(mapped: true);
        var originalCookie = await fixture.SignIn(0);
        var flow = await fixture.BeginMappingTest(originalCookie);
        var replacementCookie = await fixture.SignIn(0);
        var response = await fixture.Complete(flow with { Cookie = flow.Cookie.Replace(originalCookie, replacementCookie, StringComparison.Ordinal) });
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        using var session = fixture.Database.OpenSession();
        Assert.Empty(await session.Query<ExternalIdentity>().ToListAsync());
    }

    [SearchPostgresFact]
    public async Task Mapping_test_authenticates_provider_without_creating_or_linking_an_account()
    {
        await using var fixture = await OidcFixture.Create(mapped: true);
        var cookie = await fixture.SignIn(0);
        var flow = await fixture.BeginMappingTest(cookie);
        var result = await fixture.Complete(flow);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Contains("Pravidlo odpovídá", await result.Content.ReadAsStringAsync());
        using var session = fixture.Database.OpenSession();
        Assert.Equal(1, await session.Query<UserAccount>().CountAsync());
        Assert.Empty(await session.Query<ExternalIdentity>().ToListAsync());
    }

    [SearchPostgresFact]
    public async Task Verified_mapping_grants_existing_account_and_revokes_only_its_automatic_privilege()
    {
        await using var fixture = await OidcFixture.Create(mapped: true);
        fixture.Provider.Role = "Reader";
        await fixture.Complete(await fixture.Begin());
        Guid id;
        using (var session = fixture.Database.OpenSession())
        {
            var external = Assert.Single(await session.Query<UserAccount>().Where(x => x.Username != "admin").ToListAsync());
            id = external.Id;
            Assert.False(external.IsWikiAdmin);
        }
        fixture.Provider.Role = "Admin";
        await fixture.Complete(await fixture.Begin());
        string stamp;
        using (var session = fixture.Database.OpenSession())
        {
            var external = await session.GetAsync<UserAccount>(id);
            Assert.True(external.IsWikiAdmin);
            stamp = external.SecurityStamp;
        }
        fixture.Provider.Role = "Administrators";
        await fixture.Complete(await fixture.Begin());
        using (var session = fixture.Database.OpenSession())
        {
            var external = await session.GetAsync<UserAccount>(id);
            Assert.False(external.IsWikiAdmin);
            Assert.NotEqual(stamp, external.SecurityStamp);
            Assert.True((await session.GetAsync<UserAccount>(fixture.Admin.Id)).IsWikiAdmin);
        }
    }

    [Theory]
    [InlineData("https://evil.test")][InlineData("//evil.test")][InlineData("/\\evil.test")][InlineData("/%2f%2fevil.test")][InlineData("/\r\nLocation: evil")]
    public void Return_urls_fail_closed(string value) => Assert.Equal("/", OidcReturnUrl.Safe(value));
    [Theory][InlineData("/")][InlineData("/page/abc?mode=view")]
    public void Local_return_urls_survive(string value) => Assert.Equal(value, OidcReturnUrl.Safe(value));
    [Fact]
    public void Configuration_rejects_insecure_issuer_duplicate_provider_and_disabled_auth()
    {
        Assert.Throws<InvalidOperationException>(() => new AtlasOidcOptions { BuiltinEnabled = false }.Validate());
        var provider = new AtlasOidcProvider { Id = "hub", DisplayName = "Hub", ClientId = "wiki", Issuer = "http://hub.test" };
        var options = new AtlasOidcOptions { PublicOrigin = "https://atlas.test", Providers = [provider] };
        Assert.Throws<InvalidOperationException>(options.Validate);
        provider.Issuer = "https://hub.test"; options.Validate();
        options.Providers = [provider, provider]; Assert.Throws<InvalidOperationException>(options.Validate);
        options.Providers = [provider]; provider.EnableProviderLogout = true; Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [SearchPostgresFact]
    public async Task State_store_survives_sessions_and_rejects_concurrent_redemption()
    {
        await using var fixture = await OidcFixture.Create();
        var store = new NHibernateOidcTokenStore(fixture.Database);
        var token = new OidcStateToken { Status = "valid", Type = "state_token", CreationDate = DateTime.UtcNow, ExpirationDate = DateTime.UtcNow.AddMinutes(10) };
        await store.CreateAsync(token, default);
        var first = await store.FindByIdAsync(token.Id, default); var second = await store.FindByIdAsync(token.Id, default);
        Assert.NotNull(first); Assert.NotNull(second);
        first.Status = "redeemed"; first.RedemptionDate = DateTime.UtcNow;
        await store.UpdateAsync(first, default);
        second.Status = "redeemed";
        await Assert.ThrowsAsync<OpenIddictExceptions.ConcurrencyException>(async () => await store.UpdateAsync(second, default));
        Assert.Equal("redeemed", (await store.FindByIdAsync(token.Id, default))!.Status);
    }

    [Trait("Category", "Smoke")]
    [SearchPostgresFact]
    public async Task Real_client_code_flow_verifies_pkce_signature_nonce_and_single_use_state()
    {
        await using var fixture = await OidcFixture.Create();
        var flow = await fixture.Begin();
        Assert.Equal("S256", flow.Query["code_challenge_method"]);
        Assert.Equal("code", flow.Query["response_type"]);
        var callback = await fixture.Complete(flow);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/page/test", callback.Headers.Location!.OriginalString);
        Assert.Contains(callback.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
        Assert.True(fixture.Provider.VerifiedPkce);
        Assert.True(fixture.Provider.VerifiedClientAuthentication);
        using (var s = fixture.Database.OpenSession())
        {
            var users = await s.Query<UserAccount>().ToListAsync();
            var external = Assert.Single(users, u => u.Username.StartsWith("external_", StringComparison.Ordinal));
            Assert.False(external.IsWikiAdmin);
            Assert.Empty(await s.Query<SpaceGrant>().Where(g => g.UserId == external.Id).ToListAsync());
            Assert.Single(await s.Query<ExternalIdentity>().Where(i => i.UserId == external.Id).ToListAsync());
        }
        var replay = await fixture.Complete(flow);
        Assert.Equal("/?authError=external_login_failed", replay.Headers.Location?.OriginalString);
    }

    [Trait("Category", "Smoke")]
    [SearchPostgresFact]
    public async Task Client_rejects_wrong_nonce_audience_issuer_signature_and_missing_cookie()
    {
        await using var fixture = await OidcFixture.Create();
        foreach (var attack in new[] { "nonce", "audience", "issuer", "signature", "cookie", "state" })
        {
            fixture.Provider.Attack = attack;
            var flow = await fixture.Begin();
            var response = await fixture.Complete(flow, attack == "cookie", attack == "state");
            Assert.Equal("/?authError=external_login_failed", response.Headers.Location?.OriginalString);
            Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal)));
        }
        using var session = fixture.Database.OpenSession();
        Assert.Empty(await session.Query<ExternalIdentity>().ToListAsync());
    }

    [SearchPostgresFact]
    public async Task External_accounts_never_link_by_name_and_explicit_link_cannot_steal_identity()
    {
        await using var fixture = await OidcFixture.Create();
        var p = new AtlasOidcProvider { Issuer = "https://idp.test", AllowSignup = true };
        Task<UserAccount> Resolve(string subject, string? displayName, UserAccount? link)
            => fixture.Invoke<UserAccount>(new ResolveExternalIdentity(p.Issuer, p.AllowSignup, subject, displayName, link?.Id, link?.SecurityStamp));
        var external = await Resolve("remote-sub", "admin", null);
        Assert.NotEqual(fixture.Admin.Id, external.Id); Assert.False(external.IsWikiAdmin);
        Assert.Equal(external.Id, (await Resolve("remote-sub", "renamed", null)).Id);
        await Assert.ThrowsAsync<ApiException>(() => Resolve("remote-sub", null, fixture.Admin));
        var linked = await Resolve("admin-remote", null, fixture.Admin);
        Assert.Equal(fixture.Admin.Id, linked.Id);
        Assert.True(linked.IsWikiAdmin);
        Assert.Null(linked.OidcAdminIssuer);
        await fixture.CheckReadyAsync(new AtlasOidcOptions { BuiltinEnabled = false, Providers = [p] });
        p.AllowSignup = false;
        await Assert.ThrowsAsync<ApiException>(() => Resolve("unregistered", null, null));
    }

    [SearchPostgresFact]
    public async Task Link_requires_fresh_proof_and_same_account_on_callback()
    {
        await using var fixture = await OidcFixture.Create();
        var stale = await fixture.SignIn(600);
        var denied = await fixture.StartLink(stale);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var fresh = await fixture.SignIn(0);
        var response = await fixture.StartLink(fresh);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
        var query = QueryHelpers.ParseQuery(new Uri(data.GetProperty("loginUrl").GetString()!).Query).ToDictionary(x => x.Key, x => x.Value.ToString());
        var correlation = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        var withoutLocal = await fixture.Complete(new(query, correlation));
        Assert.Equal("/?authError=external_login_failed", withoutLocal.Headers.Location?.OriginalString);

        response = await fixture.StartLink(fresh);
        data = await response.Content.ReadFromJsonAsync<JsonElement>();
        query = QueryHelpers.ParseQuery(new Uri(data.GetProperty("loginUrl").GetString()!).Query).ToDictionary(x => x.Key, x => x.Value.ToString());
        correlation = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        var linked = await fixture.Complete(new(query, correlation + "; " + fresh));
        Assert.Equal("/", linked.Headers.Location?.OriginalString);
        using var session = fixture.Database.OpenSession();
        var identity = Assert.Single(await session.Query<ExternalIdentity>().ToListAsync());
        Assert.Equal(fixture.Admin.Id, identity.UserId);
        Assert.Single(await session.Query<UserAccount>().ToListAsync());
    }

    [SearchPostgresFact]
    public async Task Pruning_and_revocation_preserve_live_state_and_update_versions()
    {
        await using var fixture = await OidcFixture.Create(); var store = new NHibernateOidcTokenStore(fixture.Database);
        var live = new OidcStateToken { CreationDate = DateTime.UtcNow.AddDays(-2), ExpirationDate = DateTime.UtcNow.AddMinutes(10), Status = "valid", Subject = "live" };
        var expired = new OidcStateToken { CreationDate = DateTime.UtcNow.AddDays(-2), ExpirationDate = DateTime.UtcNow.AddDays(-1), Status = "valid" };
        await store.CreateAsync(live, default); await store.CreateAsync(expired, default);
        Assert.Equal(1, await store.PruneAsync(DateTimeOffset.UtcNow.AddDays(-1), default));
        Assert.NotNull(await store.FindByIdAsync(live.Id, default)); Assert.Null(await store.FindByIdAsync(expired.Id, default));
        Assert.Equal(1, await store.RevokeBySubjectAsync("live", default));
        var revoked = await store.FindByIdAsync(live.Id, default); Assert.Equal("revoked", revoked!.Status); Assert.True(revoked.Version > live.Version);
        await Assert.ThrowsAsync<OpenIddictExceptions.ConcurrencyException>(async () => await store.UpdateAsync(live, default));
    }
}

internal sealed record OidcFlow(Dictionary<string, string> Query, string Cookie);
internal sealed class OidcFixture : IAsyncDisposable
{
    private Platform.DisposableSchema m_schema = null!;
    private TestServer m_server = null!;
    private WebApplication m_host = null!;
    private HttpClient m_browser = null!;
    public AtlasDatabase Database { get; private set; } = null!;
    public TestIdentityProvider Provider { get; } = new();
    public UserAccount Admin { get; } = new() { Id = Guid.NewGuid(), Username = "admin", DisplayName = "Admin", IsWikiAdmin = true };
    public static async Task<OidcFixture> Create(bool mapped = false, string claimType = "role", bool linkAdmin = false)
    {
        var f = new OidcFixture();
        f.m_schema = await Platform.DisposableSchema.CreateMigratedAsync("atlas_oidc_");
        var connection = new NpgsqlConnectionStringBuilder(f.m_schema.ConnectionString);
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:atlas-db"] = connection.ConnectionString,
            ["Authentication:OpenIdConnect:PublicOrigin"] = "https://atlas.test",
            ["Authentication:OpenIdConnect:Providers:0:Id"] = "test",
            ["Authentication:OpenIdConnect:Providers:0:DisplayName"] = "Test",
            ["Authentication:OpenIdConnect:Providers:0:Issuer"] = "https://idp.test/",
            ["Authentication:OpenIdConnect:Providers:0:ClientId"] = "atlas-test",
            ["Authentication:OpenIdConnect:Providers:0:ClientSecret"] = "fixture-only",
            ["Authentication:OpenIdConnect:Providers:0:AllowSignup"] = "true"
        };
        // .NET 11 binds a key with a null value as an element of the array, so the fixture without a mapping names none.
        if (mapped)
        {
            settings["Authentication:OpenIdConnect:Providers:0:AdminMappings:0:ClaimType"] = claimType;
            settings["Authentication:OpenIdConnect:Providers:0:AdminMappings:0:ClaimValue"] = "Admin";
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Host.UseWolverine(options => options.ConfigureAtlasWolverine());
        var services = builder.Services;
        {
            services.AddSingleton<IConfiguration>(config);
            services.AddAtlasPlatform();
            services.AddAuth();
            services.AddRouting(); services.AddLogging(b => b.ClearProviders());
            services.AddRateLimiter(o => o.AddFixedWindowLimiter("authentication", l => { l.PermitLimit = 1000; l.Window = TimeSpan.FromMinutes(1); }));
            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
            services.AddOpenIdConnect(config, new TestEnvironment());
            services.AddHub();
            services.AddSingleton(new Atlas.Server.Features.Security.Networks.TrustedProxyEnvironment([], false));
            services.AddSecurityCenter();
            services.AddScoped<Atlas.Server.Features.Vaults.VaultGrantSnapshots>();
            services.AddScoped<Atlas.Server.Features.Vaults.VaultAccess>();
            services.AddAuthorization();
            services.PostConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = f.Provider));
        }
        f.m_host = builder.Build();
        f.m_host.Use(async (context, next) => { try { await next(context); } catch (ApiException ex) { context.Response.StatusCode = ex.Status; } });
        f.m_host.UseRouting(); f.m_host.UseRateLimiter(); f.m_host.UseAuthentication(); f.m_host.UseAuthorization(); f.m_host.MapOpenIdConnect(); f.m_host.MapSecurity();
        // security/me alone: the rest of the vault API needs the vault runtime.
        f.m_host.MapGet("/api/security/me", async (IMessageBus bus) => await bus.InvokeAsync<Atlas.Server.Features.Vaults.Contracts.SecurityCapabilitiesView>(new Atlas.Server.Features.Vaults.Queries.GetSecurityCapabilities()));
        f.m_host.MapGet("/fixture/signin", async (int age, Microsoft.AspNetCore.Http.HttpContext context) =>
            await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignInAsync(context, CookieAuthenticationDefaults.AuthenticationScheme, SignInPrincipal.Create(f.Admin, "oidc:test", DateTimeOffset.UtcNow.AddSeconds(-age))));
        await f.m_host.StartAsync(); f.m_server = f.m_host.GetTestServer();
        f.Database = f.m_server.Services.GetRequiredService<AtlasDatabase>();
        // AtlasDatabaseInitializer is a BackgroundService after wave 0; StartAsync does not await it.
        await f.Database.InitializeAsync(default);
        using (var s = f.Database.OpenSession())
        {
            using var tx = s.BeginTransaction(); await s.SaveAsync(f.Admin); await s.SaveAsync(new Workspace { Id = 1, Name = "Fixture", CreatedAt = DateTime.UtcNow }); await tx.CommitAsync();
        }
        if (linkAdmin)
        {
            using var s = f.Database.OpenSession();
            using var tx = s.BeginTransaction();
            await s.SaveAsync(new ExternalIdentity { Id = Guid.NewGuid(), UserId = f.Admin.Id, Issuer = "https://idp.test/", Subject = "stable-remote-user", ActiveFrom = DateTime.UtcNow });
            await tx.CommitAsync();
        }
        f.m_browser = f.m_server.CreateClient(); f.m_browser.BaseAddress = new Uri("https://atlas.test");
        return f;
    }
    public async Task<OidcFlow> Begin()
    {
        var response = await m_browser.GetAsync("/api/auth/oidc/test/login?returnUrl=/page/test");
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query).ToDictionary(x => x.Key, x => x.Value.ToString());
        var cookie = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        return new(query, cookie);
    }
    public async Task<Guid> EnableHubAsync()
    {
        using var session = Database.OpenSession();
        using var tx = session.BeginTransaction();
        var connection = Guid.NewGuid();
        var group = Guid.NewGuid();
        var protector = m_host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("ScaleAtlas.Hub.Token.v1");
        await session.SaveAsync(new HubConnection { Id = connection });
        await session.SaveAsync(new HubConnectionRevision { Id = Guid.NewGuid(), ConnectionId = connection, Issuer = "https://idp.test/", BaseUrl = "https://idp.test", ProtectedToken = protector.Protect("synthetic-hub-reader"), Mode = HubConnectionMode.Enforced, ChangedById = Admin.Id, ActiveFrom = DateTime.UtcNow.AddMinutes(-1) });
        await session.SaveAsync(new UserGroup { Id = group, Name = "Hub writers" });
        await session.SaveAsync(new HubGroupBinding { GroupId = group, ConnectionId = connection, ExternalGroupId = "hub-writers" });
        await tx.CommitAsync();
        return group;
    }
    public async Task<OidcFlow> BeginMappingTest(string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/oidc/test/test-mapping");
        request.Headers.Add("Cookie", cookie);
        var response = await m_browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query).ToDictionary(x => x.Key, x => x.Value.ToString());
        return new(query, cookie + "; " + string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));
    }
    public async Task<string> SignIn(int age)
    {
        var response = await m_browser.GetAsync("/fixture/signin?age=" + age);
        return string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
    }
    public async Task<T> Invoke<T>(object message)
    {
        using var scope = m_host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>().InvokeAsync<T>(message);
    }

    public async Task CheckReadyAsync(AtlasOidcOptions options)
    {
        using var session = Database.OpenSession();
        using var transaction = session.BeginTransaction();
        await new CheckAuthenticationReadyHandler(session, options).Handle(new CheckAuthenticationReady(), default);
        await transaction.CommitAsync();
    }

    /// <summary>POST /api/security/reauth with the session cookie; the flow carries the session and the correlation cookies.</summary>
    public async Task<(JsonElement Started, OidcFlow Flow)> StartReauth(string cookie, string path = "/api/security/reauth", object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { operation = "security", returnUrl = "/settings" }) };
        request.Headers.Add("Cookie", cookie);
        var response = await m_browser.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var started = await response.Content.ReadFromJsonAsync<JsonElement>();
        var query = QueryHelpers.ParseQuery(new Uri(started.GetProperty("redirectUrl").GetString()!).Query).ToDictionary(x => x.Key, x => x.Value.ToString());
        return (started, new(query, cookie + "; " + string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]))));
    }
    public async Task<JsonElement> Get(string path, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        var response = await m_browser.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    public async Task<HttpResponseMessage> StartLink(string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/profile/identities/test/link") { Content = JsonContent.Create(new { returnUrl = "/" }) };
        request.Headers.Add("Cookie", cookie); return await m_browser.SendAsync(request);
    }
    public async Task<HttpResponseMessage> Complete(OidcFlow flow, bool omitCookie = false, bool tamperState = false)
    {
        var code = Guid.NewGuid().ToString("N"); Provider.Codes[code] = flow.Query;
        var state = flow.Query["state"] + (tamperState ? "x" : "");
        var request = new HttpRequestMessage(HttpMethod.Get, "/signin-oidc/test?code=" + code + "&state=" + Uri.EscapeDataString(state) + "&iss=https%3A%2F%2Fidp.test%2F");
        if (!omitCookie)
        {
            request.Headers.Add("Cookie", flow.Cookie);
        }
        return await m_browser.SendAsync(request);
    }
    public async ValueTask DisposeAsync()
    {
        m_browser?.Dispose(); if (m_host is not null) await m_host.DisposeAsync();
        if (m_schema is not null) await m_schema.DisposeAsync();
    }
    private sealed class TestEnvironment : IHostEnvironment
    { public string EnvironmentName { get; set; } = "Development"; public string ApplicationName { get; set; } = "OidcFixture"; public string ContentRootPath { get; set; } = AppContext.BaseDirectory; public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider(); }
}

// In-process HTTPS transport fixture: the real OpenIddict discovery/JWKS/JWT pipeline runs
// unchanged; only network I/O is replaced. No real company credentials are needed.
internal sealed class TestIdentityProvider : HttpMessageHandler
{
    public string Role { get; set; } = "Admin";
    private readonly RSA rsa = RSA.Create(2048);
    public ConcurrentDictionary<string, Dictionary<string, string>> Codes { get; } = new();
    public string? Attack { get; set; }
    /// <summary>The id_token auth_time claim; null leaves it out, as the fixture always did.</summary>
    public DateTimeOffset? AuthTime { get; set; }
    public bool VerifiedPkce { get; private set; }
    public bool VerifiedClientAuthentication { get; private set; }
    public bool HubMember { get; set; } = true;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath == "/api/rest/users/me")
            return Json(new { id = "reader", banned = false, guest = false, transitiveProjectRoles = new[] { new { project = new { id = "0", global = true }, role = new { permissions = new[] { new { key = "jetbrains.jetpass.user-read-basic" }, new { key = "jetbrains.jetpass.group-read" }, new { key = "jetbrains.jetpass.role-read" } } } } } });
        if (request.RequestUri.AbsolutePath == "/api/rest/users/stable-remote-user")
            return Json(new { id = "stable-remote-user", banned = false, guest = false, transitiveGroups = HubMember ? new[] { new { id = "hub-writers", name = "Hub writers" } } : [] });
        if (request.RequestUri!.AbsolutePath == "/.well-known/openid-configuration")
        {
            return Json(new
            {
                issuer = "https://idp.test/", authorization_endpoint = "https://idp.test/authorize", token_endpoint = "https://idp.test/token", jwks_uri = "https://idp.test/jwks",
                response_types_supported = new[] { "code" }, subject_types_supported = new[] { "public" }, id_token_signing_alg_values_supported = new[] { "RS256" },
                scopes_supported = new[] { "openid", "profile" }, grant_types_supported = new[] { "authorization_code" }, token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic" }, code_challenge_methods_supported = new[] { "S256" }, authorization_response_iss_parameter_supported = true
            });
        }
        if (request.RequestUri.AbsolutePath == "/jwks")
        {
            var key = rsa.ExportParameters(false);
            return Json(new { keys = new[] { new { kty = "RSA", use = "sig", kid = "test-key", alg = "RS256", n = Base64UrlEncoder.Encode(key.Modulus!), e = Base64UrlEncoder.Encode(key.Exponent!) } } });
        }
        if (request.RequestUri.AbsolutePath == "/token")
        {
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
            var basic = request.Headers.Authorization;
            VerifiedClientAuthentication = basic?.Scheme == "Basic" && Encoding.UTF8.GetString(Convert.FromBase64String(basic.Parameter!)) == "atlas-test:fixture-only" || form["client_id"] == "atlas-test" && form["client_secret"] == "fixture-only";
            if (!VerifiedClientAuthentication)
            {
                return Json(new { error = "invalid_client" }, HttpStatusCode.Unauthorized);
            }
            if (!Codes.TryRemove(form["code"].ToString(), out var original))
            {
                return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
            }
            var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())));
            VerifiedPkce = challenge == original["code_challenge"];
            if (!VerifiedPkce || form["redirect_uri"] != "https://atlas.test/signin-oidc/test")
            {
                return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
            }
            using var attacker = RSA.Create(2048);
            var signing = new RsaSecurityKey(Attack == "signature" ? attacker : rsa) { KeyId = "test-key" };
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Attack == "issuer" ? "https://evil.test/" : "https://idp.test/", Audience = Attack == "audience" ? "wrong-client" : "atlas-test",
                IssuedAt = DateTime.UtcNow, Expires = DateTime.UtcNow.AddMinutes(5), SigningCredentials = new SigningCredentials(signing, SecurityAlgorithms.RsaSha256),
                Claims = WithAuthTime(new Dictionary<string, object> { ["sub"] = "stable-remote-user", ["name"] = "Remote User", ["email"] = "admin@example.test", ["role"] = Role, ["groups"] = new[] { "staff", Role }, ["nonce"] = Attack == "nonce" ? "wrong-nonce" : original["nonce"] })
            });
            return Json(new { access_token = "fixture-unused-access-token", token_type = "Bearer", expires_in = 300, id_token = token });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
    private Dictionary<string, object> WithAuthTime(Dictionary<string, object> claims)
    {
        if (AuthTime is { } authTime)
        {
            claims["auth_time"] = authTime.ToUnixTimeSeconds();
        }
        return claims;
    }
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
}
