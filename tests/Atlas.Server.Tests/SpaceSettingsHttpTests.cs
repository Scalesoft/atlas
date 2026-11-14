using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;
namespace Atlas.Server.Tests;
public sealed class SpaceSettingsHttpTests
{
    [PostgreSqlFact]
    public async Task Settings_cookie_routes_require_CSRF_keep_admin_boundary_and_expose_description_to_accessible_space_list()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_space_settings_http_");
        using var admin = site.Client();
        var user = await site.CompleteSetupAsync(admin);
        var id = await AtlasHttpSite.OwnSpaceAsync(admin); var path = $"/api/spaces/{id}/settings";
        var engineering = TestLogin.For("Engineering");
        var current = await Json(await admin.GetAsync(path), HttpStatusCode.OK);
        var request = new { name=engineering, icon="code-xml", color="green", description="Týmová dokumentace", pinPolicy="None", expectedVersion=current["version"]!.GetValue<string>() };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(path,request)).StatusCode);
        var updated = await Json(await Send(admin,HttpMethod.Put,path,request),HttpStatusCode.OK);
        Assert.NotEqual(current["version"]!.GetValue<string>(), updated["version"]!.GetValue<string>());
        Assert.Equal("Týmová dokumentace", updated["description"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(admin,HttpMethod.Put,path,request)).StatusCode);
        // Listed as a member: a wiki administrator lists every test's spaces, only the first 200 by name.
        var memberLogin = TestLogin.For("member");
        var invitation = await Json(await Send(admin, HttpMethod.Post, "/api/admin/users", new { username = memberLogin, displayName = "Member", isWikiAdmin = false }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(admin, HttpMethod.Put, $"/api/spaces/{id}/access",
            new { userId = invitation["user"]!["id"]!.GetValue<Guid>(), role = "Reader", expectedVersion = (string?)null })).StatusCode);
        using var member = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(member, HttpMethod.Post, "/api/auth/activate", new { token = invitation["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        await Json(await Send(member, HttpMethod.Post, "/api/auth/login", new { username = memberLogin, password = site.Password }), HttpStatusCode.OK);
        var listed = await Json(await member.GetAsync("/api/spaces"), HttpStatusCode.OK);
        var listedSpace = Assert.Single(listed.AsArray(), x => x!["id"]!.GetValue<string>() == id)!;
        Assert.Equal(engineering, listedSpace["name"]!.GetValue<string>());
        Assert.Equal("Týmová dokumentace", listedSpace["description"]!.GetValue<string>());
        var created = await Json(await Send(admin, HttpMethod.Post, "/api/spaces", new { name = $" {TestLogin.For("New")} ", icon = "compass", color = "amber", groups = (object?)null }), HttpStatusCode.OK);
        Assert.StartsWith("New-", created["name"]!.GetValue<string>());
        Assert.Equal("Admin", created["role"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/spaces/{created["id"]!.GetValue<string>()}/settings")).StatusCode);
        await Json(await Send(admin,HttpMethod.Put,$"/api/admin/users/{user["id"]!.GetValue<string>()}/api-access",new{canIssueTokens=true,expectedVersion=(string?)null}),HttpStatusCode.OK);
        await AtlasHttpSite.ReauthAsync(admin, site.Password);
        var token=await Json(await Send(admin,HttpMethod.Post,"/api/profile/api-tokens",new{name="Reader",scopes=new[]{"read"},expiresInDays=1}),HttpStatusCode.Created);
        using var api=site.Client(); api.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token["secret"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Unauthorized,(await api.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await api.GetAsync(path.Replace("/api/","/api/v1/",StringComparison.Ordinal))).StatusCode);
    }
    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? payload = null) => AtlasHttpSite.SendAsync(client, method, path, payload);
    private static Task<JsonNode> Json(HttpResponseMessage response, HttpStatusCode expected) => AtlasHttpSite.Json(response, expected);
}
