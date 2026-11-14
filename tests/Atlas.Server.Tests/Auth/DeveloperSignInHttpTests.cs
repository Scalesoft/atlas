using System.Net;
using Atlas.Server.Tests.Platform;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace Atlas.Server.Tests.Auth;

/// <summary>
/// "Přihlásit jako vývojář" opens a local build without credentials, so it stands behind three guards that are all required on every
/// request: the Development environment (the route is not even mapped elsewhere), the Authentication:DeveloperSignIn:Enabled switch
/// and a loopback TCP peer without any forwarded header. A refusal is a plain 404, the same answer as a route that does not exist,
/// so the guard is no oracle for anyone probing. A permitted call is an ordinary builtin-style sign-in of the oldest active wiki
/// administrator through ISignInService, with its sign-in fact.
/// </summary>
public sealed class DeveloperSignInHttpTests
{
    private const string Switch = "Authentication:DeveloperSignIn:Enabled";
    private const string Route = "/api/auth/developer";

    /// <summary>TestServer has no TCP peer; this sets one per request (X-Test-Peer, loopback by default) before the pipeline, as Kestrel would.</summary>
    private sealed class PeerStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Peer"].FirstOrDefault() ?? "127.0.0.1");
                await inner();
            });
            next(app);
        };
    }

    private static Task<AtlasHttpSite> SiteAsync(string prefix, bool enabled, string? network = null) =>
        AtlasHttpSite.CreateAsync(prefix, settings =>
        {
            settings[Switch] = enabled ? "true" : "false";
            if (network is not null) settings["Authentication:DeveloperSignIn:Networks:0"] = network;
        }, services =>
        {
            services.AddSingleton<IStartupFilter, PeerStartupFilter>();
            // A Production host loads build-time handler code and refuses to start without it; the tests generate it at runtime.
            services.CritterStackDefaults(defaults =>
            {
                defaults.Production.GeneratedCodeMode = TypeLoadMode.Dynamic;
                defaults.Production.AssertAllPreGeneratedTypesExist = false;
            });
        });

    private static async Task<bool> Offered(HttpClient client)
        => (await AtlasHttpSite.Json(await client.GetAsync("/api/auth/methods"), HttpStatusCode.OK))["developerSignIn"]?.GetValue<bool>() == true;

    private static async Task AssertRefusedAsync(HttpClient client)
    {
        Assert.False(await Offered(client));
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(client, HttpMethod.Post, Route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Production_has_no_developer_sign_in_even_with_the_switch_on_and_a_loopback_peer()
    {
        await using var site = await SiteAsync("atlas_dev_signin_production_", enabled: true);
        await using var production = site.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = production.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true
        });
        await AssertRefusedAsync(client);
        Assert.DoesNotContain(production.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == Route);
    }

    [PostgreSqlFact]
    public async Task Development_without_the_switch_refuses_and_offers_nothing()
    {
        await using var site = await SiteAsync("atlas_dev_signin_off_", enabled: false);
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        using var anonymous = site.Client();
        await AssertRefusedAsync(anonymous);
    }

    /// <summary>Raw transport evidence decides, not the address the forwarded headers resolve to: a remote peer is refused, and so is a
    /// loopback peer that carries any forwarding header, since that is a proxy on this host passing on someone else's request.</summary>
    [PostgreSqlFact]
    public async Task Remote_peers_and_forwarded_requests_are_refused()
    {
        await using var site = await SiteAsync("atlas_dev_signin_remote_", enabled: true);
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        (string Name, string Value)[] origins =
        [
            ("X-Test-Peer", "203.0.113.7"), ("X-Test-Peer", "192.168.1.20"), ("X-Forwarded-For", "127.0.0.1"),
            ("Forwarded", "for=127.0.0.1"), ("X-Forwarded-Proto", "http")
        ];
        foreach (var (name, value) in origins)
        {
            using var client = site.Client();
            client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
            await AssertRefusedAsync(client);
        }
    }

    /// <summary>A shared development instance may name its internal network: peers there are offered the sign-in, others and
    /// forwarded requests still are not.</summary>
    [PostgreSqlFact]
    public async Task A_named_internal_network_may_use_the_developer_sign_in_and_nothing_else_does()
    {
        await using var site = await SiteAsync("atlas_dev_signin_net_", enabled: true, network: "10.0.0.0/8");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        using var inside = site.Client();
        inside.DefaultRequestHeaders.TryAddWithoutValidation("X-Test-Peer", "10.5.2.200");
        Assert.True(await Offered(inside));
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(inside, HttpMethod.Post, Route), HttpStatusCode.OK);
        (string Name, string Value)[][] refused =
        [
            [("X-Test-Peer", "192.168.1.20")],
            [("X-Test-Peer", "10.5.2.200"), ("X-Forwarded-For", "203.0.113.7")],
        ];
        foreach (var headers in refused)
        {
            using var client = site.Client();
            foreach (var (name, value) in headers) client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
            await AssertRefusedAsync(client);
        }
    }

    [PostgreSqlFact]
    public async Task Loopback_development_sign_in_is_a_normal_session_of_the_oldest_active_wiki_admin()
    {
        await using var site = await SiteAsync("atlas_dev_signin_", enabled: true);
        using var developer = site.Client();
        Assert.True(await Offered(developer));
        var early = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(developer, HttpMethod.Post, Route), HttpStatusCode.Conflict);
        Assert.Equal("setup_required", early["code"]!.GetValue<string>());

        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        // A younger administrator whose id sorts first: the choice follows the age of the account, not its id.
        await site.ExecuteAsync("""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp")
            VALUES ('00000000-0000-0000-0000-000000000001','younger','Younger admin',true,true,'stamp-of-the-younger-admin')
            """);

        var signedIn = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(developer, HttpMethod.Post, Route), HttpStatusCode.OK);
        Assert.Equal("administrator", signedIn["username"]!.GetValue<string>());
        var me = await AtlasHttpSite.Json(await developer.GetAsync("/api/auth/me"), HttpStatusCode.OK);
        Assert.Equal("administrator", me["username"]!.GetValue<string>());
        // An authenticated mutation: the session and its CSRF binding behave as after a password login.
        var appearance = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(developer, HttpMethod.Put, "/api/profile/appearance",
            new { layout = "islands", accent = "blue", mode = "dark" }), HttpStatusCode.OK);
        Assert.Equal("islands", appearance["layout"]!.GetValue<string>());
        Assert.Equal(1L, await site.ScalarAsync("""
            SELECT count(*) FROM "UserSignIn" s JOIN "UserAccount" u ON u."Id"=s."UserId"
            WHERE u."Username"='administrator' AND s."AuthenticationMethod"='developer'
            """));

        // Only active administrators count; the next oldest takes over.
        await site.ExecuteAsync("""UPDATE "UserAccount" SET "IsActive"=false WHERE "Username"='administrator'""");
        using var second = site.Client();
        var younger = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(second, HttpMethod.Post, Route), HttpStatusCode.OK);
        Assert.Equal("younger", younger["username"]!.GetValue<string>());

        await site.ExecuteAsync("""UPDATE "UserAccount" SET "IsWikiAdmin"=false WHERE "Username"='younger'""");
        using var third = site.Client();
        var none = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(third, HttpMethod.Post, Route), HttpStatusCode.Conflict);
        Assert.Equal("no_admin", none["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await third.GetAsync("/api/auth/me")).StatusCode);
    }
}
