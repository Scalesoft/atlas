using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Auth;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Security;
using Atlas.Server.Features.Security.Commands;
using Atlas.Server.Features.Security.Networks;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Security;

/// <summary>
/// Real PostgreSQL, Wolverine and the real security change guard. The request is a fixed HttpContext (claims with the Atlas session,
/// the X-Atlas-Reauth header, the TCP peer and forwarded headers) that every handler sees. Seeds a WikiAdmin with a builtin
/// password, a space Admin, and one vault with two secrets as rows (no KDBX file needed for protection).
/// </summary>
internal sealed class SecurityFixture : IAsyncDisposable
{
    public const string AdminPassword = "fixture-admin-password";

    private SecurityFixture(AtlasTestHost host, IHttpContextAccessor http)
    {
        Host = host;
        Http = http;
    }

    public AtlasTestHost Host { get; }
    /// <summary>The request of this test: an AsyncLocal accessor, so tests sharing the host each see their own.</summary>
    public IHttpContextAccessor Http { get; }
    public UserAccount Admin { get; private set; } = null!;
    public UserAccount SpaceAdmin { get; private set; } = null!;
    public Guid SpaceId { get; private set; }
    public Guid VaultId { get; private set; }
    public Guid SecretId { get; private set; }
    public Guid OtherSecretId { get; private set; }

    /// <param name="shared">One host for every test that passes it: only for a test that neither changes nor counts the installation's
    /// security (the SecurityConfiguration row Id 1 and its Version, the instance protection mode, TrustedAddress, fresh-auth provider
    /// verdicts). Without it the test gets a host and database of its own.</param>
    public static async Task<SecurityFixture> CreateAsync(IReadOnlyList<IPAddress>? environmentProxies = null, bool recover = false,
        AtlasOidcOptions? oidc = null, DisposableSchema? schema = null, bool shared = false)
    {
        var http = new HttpContextAccessor();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Vaults:RefreshSeconds"] = "0" })
            .Build();
        void Configure(IServiceCollection services)
        {
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddVaults(configuration);
            services.AddSingleton(oidc ?? new AtlasOidcOptions());
            services.AddSingleton(new TrustedProxyEnvironment(environmentProxies ?? [], recover));
            services.AddAuth();
            services.AddSecurityCenter();
            services.AddSingleton<IHttpContextAccessor>(http);
        }
        var host = schema is not null ? await AtlasTestHost.CreateAsync(schema, Configure)
            : shared ? await AtlasTestHost.CreateAsync("atlas_security_", Configure, sharedAs: "security")
            // Own database: the test changes or counts the installation's security configuration (see shared).
            : await AtlasTestHost.CreateIsolatedAsync("atlas_security_", Configure);
        // The accessor of the shared host's first fixture; its AsyncLocal keeps each test's request apart.
        var fixture = new SecurityFixture(host, host.Services.GetRequiredService<IHttpContextAccessor>());
        await fixture.SeedAsync();
        return fixture;
    }

    private async Task SeedAsync()
    {
        Admin = await Host.SeedUserAsync("wiki-admin", isWikiAdmin: true);
        SpaceAdmin = await Host.SeedUserAsync("space-admin");
        SpaceId = await Host.SeedSpaceAsync("Provoz", (SpaceAdmin, SpaceRole.Admin));
        var hasher = Host.Services.GetRequiredService<IPasswordHasher<UserAccount>>();
        VaultId = Guid.NewGuid();
        SecretId = Guid.NewGuid();
        OtherSecretId = Guid.NewGuid();
        await Host.SeedAsync(async session =>
        {
            await session.CreateSQLQuery("""INSERT INTO "Workspace"("Id","Name","CreatedAt") VALUES (1,'Security',now()) ON CONFLICT DO NOTHING""")
                .ExecuteUpdateAsync();
            await session.SaveAsync(new BuiltinCredential { UserId = Admin.Id, PasswordHash = hasher.HashPassword(Admin, AdminPassword) });
            await session.FlushAsync();
            var location = Guid.NewGuid();
            await session.CreateSQLQuery("""
                INSERT INTO "VaultLocation"("Id","Name","Kind","Root") VALUES (:location,'Místní ' || :location,'local','C:\vaults');
                INSERT INTO "Vault"("Id","SpaceId","Name","LocationId","FileName","State","CreatedBy","CreatedAt")
                VALUES (:vault,:space,'Provozní hesla',:location,'provoz.kdbx','locked',:admin,now());
                INSERT INTO "VaultSecret"("Id","VaultId","EntryUuid","Title","GroupPath","AvailableFields","FirstSeenAt")
                VALUES (:secret,:vault,gen_random_uuid(),'Router','Síť',3,now()), (:other,:vault,gen_random_uuid(),'Pošta','',3,now());
                """).SetParameter("location", location).SetParameter("vault", VaultId).SetParameter("space", SpaceId)
                .SetParameter("admin", Admin.Id).SetParameter("secret", SecretId).SetParameter("other", OtherSecretId).ExecuteUpdateAsync();
        });
    }

    /// <summary>The request: this account, its current stamp, an Atlas session and sign-in method, the TCP peer and headers.</summary>
    public void SignIn(UserAccount user, string session = "session-1", string method = "builtin", string? peer = "203.0.113.7",
        IDictionary<string, string>? headers = null)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username),
                new Claim("stamp", user.SecurityStamp), new Claim("atlas_session", session), new Claim("authentication_method", method)
            ], "test"))
        };
        context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            context.Request.Headers[name] = value;
        }
        Http.HttpContext = context;
        Host.Actor.Current = user;
    }

    /// <summary>Presents a proof (or none) with the next request.</summary>
    public void Present(Guid? proof)
    {
        Http.HttpContext!.Request.Headers.Remove("X-Atlas-Reauth");
        if (proof is { } id)
        {
            Http.HttpContext.Request.Headers["X-Atlas-Reauth"] = id.ToString();
        }
    }

    public Task<T> Invoke<T>(object message) => Host.InvokeAsync<T>(message);

    public Task Invoke(object message) => Host.InvokeAsync(message);

    /// <summary>A verified password proof of the signed-in account, presented with the next request.</summary>
    public async Task<Guid> ProveAsync(string operation = "security", Guid? vaultId = null, Guid? secretId = null, string password = AdminPassword)
    {
        var started = await Invoke<ReauthStarted>(new StartReauth(operation, vaultId, secretId));
        Assert.Equal("password", started.Method);
        var outcome = await Invoke<ReauthOutcome>(new VerifyReauthPassword(started.Id, password));
        Assert.True(outcome.Verified, outcome.Failure);
        Present(started.Id);
        return started.Id;
    }

    public async Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters)
    {
        using var session = Host.OpenSession();
        var query = session.CreateSQLQuery(sql);
        foreach (var (name, value) in parameters)
        {
            query.SetParameter(name, value);
        }
        return Convert.ToInt64(await query.UniqueResultAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
