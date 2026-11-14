using System.Net;
using System.Net.Http.Json;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Hub;

public sealed class HubHttpTests
{
    [PostgreSqlFact]
    public async Task Module_contract_redacts_token_and_enforces_fresh_auth_when_enabling()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_hub_http_", settings =>
        {
            settings["Authentication:OpenIdConnect:PublicOrigin"] = "https://localhost";
            settings["Authentication:OpenIdConnect:Providers:0:Id"] = "hub";
            settings["Authentication:OpenIdConnect:Providers:0:DisplayName"] = "Hub";
            settings["Authentication:OpenIdConnect:Providers:0:Issuer"] = "https://hub.example.test/hub";
            settings["Authentication:OpenIdConnect:Providers:0:ClientId"] = "atlas";
        }, services => services.AddHttpClient("hub-directory").ConfigurePrimaryHttpMessageHandler(() => new ReadOnlyHub()));
        using var admin = site.Client();
        var adminAccount = await site.CompleteSetupAsync(admin);
        var initial = await AtlasHttpSite.Json(await admin.GetAsync("/api/admin/modules/hub"), HttpStatusCode.OK);
        Assert.False(initial["hasToken"]!.GetValue<bool>());
        var savedResponse = await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/modules/hub", new
        {
            issuer = "https://hub.example.test/hub", baseUrl = "https://hub.example.test/hub", mode = "Disabled", token = "synthetic-reader-secret", expectedVersion = (string?)null
        });
        var saved = await AtlasHttpSite.Json(savedResponse, HttpStatusCode.OK);
        Assert.True(saved["hasToken"]!.GetValue<bool>());
        Assert.DoesNotContain("synthetic-reader-secret", saved.ToJsonString(), StringComparison.Ordinal);
        var enforce = new { issuer = "https://hub.example.test/hub", baseUrl = "https://hub.example.test/hub", mode = "Enforced", token = (string?)null, expectedVersion = saved["version"]!.GetValue<string>() };
        var requiresProof = await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/modules/hub", enforce);
        var refusal = await AtlasHttpSite.Json(requiresProof, HttpStatusCode.Forbidden);
        Assert.Equal("reauth_required", refusal["code"]!.GetValue<string>());
        var proof = await AtlasHttpSite.ReauthAsync(admin, site.Password);
        var enabled = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/modules/hub", enforce, proof), HttpStatusCode.OK);
        Assert.Equal("Enforced", enabled["mode"]!.GetValue<string>());
        var profile = await AtlasHttpSite.Json(await admin.GetAsync("/api/profile/hub"), HttpStatusCode.OK);
        Assert.Null(profile["identity"]);
        Assert.Equal("Enforced", profile["mode"]!.GetValue<string>());
        await site.ExecuteAsync("""
            INSERT INTO "ExternalIdentity"("Id","UserId","Issuer","Subject","ActiveFrom")
            VALUES (gen_random_uuid(),@user,'https://hub.example.test/hub','hub-user',statement_timestamp())
            """, ("user", adminAccount["id"]!.GetValue<Guid>()));
        var verified = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/profile/hub/verify"), HttpStatusCode.OK);
        Assert.Equal(1, verified["verified"]!.GetValue<int>());
        Assert.Equal("Active", (await AtlasHttpSite.Json(await admin.GetAsync("/api/profile/hub"), HttpStatusCode.OK))["identity"]!["state"]!.GetValue<string>());
        var refreshed = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/modules/hub/verify", new { userId = (Guid?)null }), HttpStatusCode.OK);
        Assert.Equal(1, refreshed["verified"]!.GetValue<int>());
    }

    private sealed class ReadOnlyHub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = request.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal)
                ? """{"id":"reader","banned":false,"guest":false,"transitiveProjectRoles":[{"project":{"id":"0","global":true},"role":{"permissions":[{"key":"jetbrains.jetpass.user-read-basic"},{"key":"jetbrains.jetpass.group-read"},{"key":"jetbrains.jetpass.role-read"}]}}]}"""
                : request.RequestUri.AbsolutePath.EndsWith("/hub-user", StringComparison.Ordinal)
                    ? """{"id":"hub-user","banned":false,"guest":false,"transitiveGroups":[]}"""
                    : """{"usergroups":[],"skip":0,"top":50,"total":0}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
