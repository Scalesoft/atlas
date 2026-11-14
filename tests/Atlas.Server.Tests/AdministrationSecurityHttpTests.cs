using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class AdministrationSecurityHttpTests
{
    [PostgreSqlFact]
    public async Task Password_reset_requires_fresh_authentication_even_for_the_administrators_own_account()
    {
        await using var site = await AtlasHttpSite.CreateAsync("reset_guard_");
        using var admin = site.Client();
        var owner = await site.CompleteSetupAsync(admin);
        // The list is paged and holds every test's accounts: ask for this one by id.
        var users = (await admin.GetFromJsonAsync<JsonArray>("/api/admin/users?id=" + owner["id"]!.GetValue<string>()))!;
        var account = Assert.Single(users, x => x!["id"]!.GetValue<string>() == owner["id"]!.GetValue<string>())!;
        var path = "/api/admin/users/" + owner["id"]!.GetValue<string>() + "/reset-password";
        var request = new { expectedVersion = account["version"]!.GetValue<string>() };
        var refused = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, path, request);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("reauth_required", await refused.Content.ReadAsStringAsync());
        // A rejected reset must not rotate credentials or the account's security stamp.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/auth/me")).StatusCode);
        var proof = await AtlasHttpSite.ReauthAsync(admin, site.Password);
        var reset = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, path, request, proof), HttpStatusCode.OK);
        Assert.NotEmpty(reset["token"]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task Membership_changes_require_fresh_authentication_and_reuse_its_window()
    {
        await using var site = await AtlasHttpSite.CreateAsync("membership_guard_");
        using var admin = site.Client();
        var owner = await site.CompleteSetupAsync(admin);
        var group = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/groups",
            new { name = TestLogin.For("Protected members") }), HttpStatusCode.OK);
        var path = "/api/admin/groups/" + group["id"]!.GetValue<string>() + "/members";
        var mutation = path + "/" + owner["id"]!.GetValue<string>();
        var add = new { isMember = true, expectedVersion = (string?)null };
        var refused = await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, mutation, add);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("reauth_required", await refused.Content.ReadAsStringAsync());
        Assert.Empty((await admin.GetFromJsonAsync<JsonArray>(path))!);
        var proof = await AtlasHttpSite.ReauthAsync(admin, site.Password);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, mutation, add, proof)).StatusCode);
        var member = Assert.Single((await admin.GetFromJsonAsync<JsonArray>(path))!)!;
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, mutation,
            new { isMember = false, expectedVersion = member["version"]!.GetValue<string>() })).StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<JsonArray>(path))!);
    }

    [PostgreSqlFact]
    public async Task Password_change_and_identity_link_share_the_login_failure_budget()
    {
        await using var site = await AtlasHttpSite.CreateAsync("password_budget_", settings =>
        {
            settings["Authentication:OpenIdConnect:PublicOrigin"] = "https://localhost";
            settings["Authentication:OpenIdConnect:Providers:0:Id"] = "acceptance";
            settings["Authentication:OpenIdConnect:Providers:0:DisplayName"] = "Acceptance provider";
            settings["Authentication:OpenIdConnect:Providers:0:Issuer"] = "https://idp.example.invalid/";
            settings["Authentication:OpenIdConnect:Providers:0:ClientId"] = "http-acceptance";
            settings["Authentication:OpenIdConnect:Providers:0:ClientSecret"] = "test-fixture-only";
        });
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        using var anonymous = site.Client();
        // The password proofs inside the session (password change, identity link) spend one budget of their own, which anonymous
        // guesses never touch: those spend the budget of their account and address (SEC-A-3, 102d026e).
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post,
            "/api/profile/password", new { currentPassword = "wrong", newPassword = site.Password + "-new" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post,
            "/api/profile/identities/acceptance/link", new { password = "wrong" })).StatusCode);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await AtlasHttpSite.SendAsync(anonymous, HttpMethod.Post,
                "/api/auth/login", new { username = "ADMINISTRATOR", password = "wrong" })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await AtlasHttpSite.SendAsync(anonymous, HttpMethod.Post,
            "/api/auth/login", new { username = "administrator", password = "wrong" })).StatusCode);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post,
                "/api/profile/password", new { currentPassword = "wrong", newPassword = site.Password + "-new" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post,
                "/api/profile/identities/acceptance/link", new { password = "wrong" })).StatusCode);
        }
        var password = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post,
            "/api/profile/password", new { currentPassword = "wrong", newPassword = site.Password + "-new" });
        Assert.Equal(HttpStatusCode.TooManyRequests, password.StatusCode);
        Assert.True(password.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        var link = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post,
            "/api/profile/identities/acceptance/link", new { password = "wrong" });
        Assert.Equal(HttpStatusCode.TooManyRequests, link.StatusCode);
        Assert.True(link.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }
}
