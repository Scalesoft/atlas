using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Security;

/// <summary>
/// Canary for the security centre API over the real host (WP4 V4): Http:TrustedProxies imported once and applied by the transport,
/// connection diagnostics behind that proxy and with a spoofed header, fresh re-authentication with the local password and its
/// throttle, networks, the tester and the instance protection. Every success is uncompressed; no body or log line
/// carries the password.
/// </summary>
public sealed class SecurityHttpTests
{
    /// <summary>TestServer has no TCP peer; this sets one per request (X-Test-Peer) before the pipeline, as Kestrel would.</summary>
    private sealed class PeerStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Peer"].FirstOrDefault() ?? "203.0.113.7");
                await inner();
            });
            next(app);
        };
    }

    [PostgreSqlFact]
    public async Task Security_api_follows_the_imported_proxies()
    {
        // Own site: imports Http:TrustedProxies and changes the installation's trusted networks (TrustedAddress) and instance
        // protection (SecurityConfiguration row Id 1).
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_security_http_", settings => settings["Http:TrustedProxies:0"] = "10.0.0.10",
            services => services.AddSingleton<IStartupFilter, PeerStartupFilter>());
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var bodies = new List<string>();
        async Task<JsonNode> Call(HttpMethod method, string path, object? payload, HttpStatusCode expected, string? reauth = null,
            IDictionary<string, string>? headers = null)
        {
            HttpResponseMessage response;
            if (method == HttpMethod.Get)
            {
                using var request = new HttpRequestMessage(method, path);
                foreach (var (name, value) in headers ?? new Dictionary<string, string>())
                {
                    request.Headers.Add(name, value);
                }
                response = await admin.SendAsync(request);
            }
            else
            {
                response = await AtlasHttpSite.SendAsync(admin, method, path, payload, reauth);
            }
            var body = await response.Content.ReadAsStringAsync();
            bodies.Add(body);
            Assert.True(expected == response.StatusCode, $"{method} {path}: {(int)response.StatusCode} {body}");
            Assert.Null(response.Content.Headers.ContentEncoding.FirstOrDefault());
            return body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!;
        }

        var proxies = await Call(HttpMethod.Get, "/api/security/proxies", null, HttpStatusCode.OK);
        Assert.Equal("10.0.0.10/32", proxies["items"]![0]!["range"]!.GetValue<string>());
        Assert.Equal((1, false), (proxies["hopLimit"]!.GetValue<int>(), proxies["environment"]!["differs"]!.GetValue<bool>()));

        var behindProxy = new Dictionary<string, string> { ["X-Test-Peer"] = "10.0.0.10", ["X-Forwarded-For"] = "198.51.100.9, 192.168.10.5" };
        var connection = await Call(HttpMethod.Get, "/api/security/connection", null, HttpStatusCode.OK, headers: behindProxy);
        Assert.Equal(("192.168.10.5", "forwarded", "10.0.0.10"),
            (connection["address"]!.GetValue<string>(), connection["source"]!.GetValue<string>(), connection["peer"]!.GetValue<string>()));
        var spoofed = await Call(HttpMethod.Get, "/api/security/connection", null, HttpStatusCode.OK,
            headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "192.168.10.5" });
        Assert.Equal(("unclear", "untrustedForwarding"), (spoofed["source"]!.GetValue<string>(), spoofed["reason"]!.GetValue<string>()));

        var version = (await Call(HttpMethod.Get, "/api/security/networks", null, HttpStatusCode.OK))["version"]!.GetValue<string>();
        var denied = await Call(HttpMethod.Post, "/api/security/networks", new { name = "Kancelář", kind = "lan", range = "192.168.10.0/24", expectedVersion = version },
            HttpStatusCode.Forbidden);
        Assert.Equal("reauth_required", denied["code"]!.GetValue<string>());
        var proof = await AtlasHttpSite.ReauthAsync(admin, site.Password);
        var saved = await Call(HttpMethod.Post, "/api/security/networks", new { name = "Kancelář", kind = "lan", range = "192.168.10.0/24", expectedVersion = version },
            HttpStatusCode.OK, proof);
        version = saved["version"]!.GetValue<string>();
        connection = await Call(HttpMethod.Get, "/api/security/connection", null, HttpStatusCode.OK, headers: behindProxy);
        Assert.Equal(("Kancelář", false, true),
            (connection["network"]!["name"]!.GetValue<string>(), connection["networkException"]!.GetValue<bool>(), connection["reauthRequired"]!.GetValue<bool>()));
        var test = await Call(HttpMethod.Post, "/api/security/networks/test", new { address = "192.168.10.9", range = "192.168.10.0/25" }, HttpStatusCode.OK);
        Assert.Equal("Kancelář", test["network"]!["name"]!.GetValue<string>());

        var instance = await Call(HttpMethod.Get, "/api/security/protection", null, HttpStatusCode.OK);
        Assert.Equal("always", instance["effective"]!.GetValue<string>());
        proof = await AtlasHttpSite.ReauthAsync(admin, site.Password);
        var changed = await Call(HttpMethod.Put, "/api/security/protection", new { mode = "outsideTrustedNetworks", expectedVersion = instance["version"]!.GetValue<string>() },
            HttpStatusCode.OK, proof);
        Assert.Equal("outsideTrustedNetworks", changed["explicit"]!.GetValue<string>());
        connection = await Call(HttpMethod.Get, "/api/security/connection", null, HttpStatusCode.OK, headers: behindProxy);
        Assert.Equal((true, false), (connection["networkException"]!.GetValue<bool>(), connection["reauthRequired"]!.GetValue<bool>()));
        await Call(HttpMethod.Get, $"/api/secrets/{Guid.NewGuid()}/protection", null, HttpStatusCode.NotFound);
        var me = await Call(HttpMethod.Get, "/api/security/me", null, HttpStatusCode.OK);
        Assert.Equal(("supported", "password", true), (me["freshAuth"]!.GetValue<string>(), me["freshAuthMethod"]!.GetValue<string>(), me["managesAllVaults"]!.GetValue<bool>()));
        Assert.NotNull(me["reauthWindowExpiresAt"]?.GetValue<DateTimeOffset>());

        // Correct passwords never spend the throttle (owner decision 28. 9.): more successes than the limit in a row all pass.
        for (var success = 0; success <= 10; success++)
        {
            await AtlasHttpSite.ReauthAsync(admin, site.Password);
        }
        // Wrong passwords answer 403 without ending the session, and the builtin login throttle (10 a minute per account) applies.
        var started = await Call(HttpMethod.Post, "/api/security/reauth", new { operation = "security" }, HttpStatusCode.OK);
        var id = started["id"]!.GetValue<string>();
        var statuses = new List<HttpStatusCode>();
        // A fixed one-minute window: at most ten permits per window, so 21 attempts always reach the limit.
        for (var attempt = 0; attempt < 21 && !statuses.Contains(HttpStatusCode.TooManyRequests); attempt++)
        {
            var response = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/security/reauth/{id}/password", new { password = "wrong-password-" + attempt });
            bodies.Add(await response.Content.ReadAsStringAsync());
            statuses.Add(response.StatusCode);
        }
        Assert.Contains(HttpStatusCode.Forbidden, statuses);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.All(statuses[..^1], status => Assert.Equal(HttpStatusCode.Forbidden, status));
        Assert.Equal("pending", (await Call(HttpMethod.Get, $"/api/security/reauth/{id}", null, HttpStatusCode.OK))["state"]!.GetValue<string>());

        // Signing out ends the session's re-authentication window on the server.
        Assert.NotEqual(0L, await site.ScalarAsync("SELECT count(*) FROM \"ReauthChallenge\" WHERE \"VerifiedAt\" IS NOT NULL AND \"WindowClosedAt\" IS NULL"));
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/auth/logout", null)).StatusCode);
        Assert.Equal(0L, await site.ScalarAsync("SELECT count(*) FROM \"ReauthChallenge\" WHERE \"VerifiedAt\" IS NOT NULL AND \"WindowClosedAt\" IS NULL"));

        var logs = string.Join("\n", site.Logs.Messages);
        foreach (var canary in new[] { site.Password, "wrong-password-" })
        {
            Assert.DoesNotContain(bodies, body => body.Contains(canary, StringComparison.Ordinal));
            Assert.DoesNotContain(canary, logs, StringComparison.Ordinal);
        }
        Assert.Equal(1L, await site.ScalarAsync("SELECT count(*) FROM \"TrustedAddress\" WHERE \"Purpose\"='proxy'"));
    }
}
