using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class AccessAuditHttpTests
{
    [PostgreSqlFact]
    public async Task Audit_endpoint_is_wiki_admin_only_and_pages_with_the_returned_cursor()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_audit_http_");
        using var anonymous = site.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/access-audit")).StatusCode);

        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        // The audit lists every test's events: this test pages through those of its own group (created, then renamed).
        var group = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/groups", new { name = TestLogin.For("Engineering") }), HttpStatusCode.OK);
        var groupId = group["id"]!.GetValue<string>();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/admin/groups/{groupId}",
            new { name = TestLogin.For("Support"), expectedVersion = group["version"]!.GetValue<string>() }), HttpStatusCode.OK);

        using var first = JsonDocument.Parse(await admin.GetStringAsync($"/api/admin/access-audit?groupId={groupId}&limit=1"));
        Assert.Equal(1, first.RootElement.GetProperty("items").GetArrayLength());
        Assert.True(first.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal("group.renamed", first.RootElement.GetProperty("items")[0].GetProperty("action").GetString());
        var cursor = first.RootElement.GetProperty("nextBeforeId").GetString();

        using var next = JsonDocument.Parse(await admin.GetStringAsync($"/api/admin/access-audit?groupId={groupId}&beforeId={cursor}&limit=1"));
        Assert.NotEqual(first.RootElement.GetProperty("items")[0].GetProperty("id").GetString(),
            next.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal("group.created", next.RootElement.GetProperty("items")[0].GetProperty("action").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/access-audit?limit=101")).StatusCode);
    }

    /// <summary>The contract the audit feed (Security centre › Činnost) reads for a write over the personal API (T3).</summary>
    [PostgreSqlFact]
    public async Task Activity_lists_api_writes_for_wiki_admins()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_audit_api_");
        using var admin = site.Client();
        var adminId = (await site.CompleteSetupAsync(admin))["id"]!.GetValue<string>();
        var (tokenId, api) = await ApiActivity.ApiActivityTests.IssueTokenAsync(site, admin, adminId, "write", ["read", "write"]);
        string pageId;
        using (api)
        {
            var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
            var page = await AtlasHttpSite.Json(await api.PostAsJsonAsync("/api/v1/pages",
                new { spaceId, parentId = (string?)null, title = "Postup VPN", markdown = "VPN" }), HttpStatusCode.Created);
            pageId = page["id"]!.GetValue<string>();
            await AtlasHttpSite.Json(await api.PostAsJsonAsync($"/api/v1/pages/{pageId}/publish", new { expectedVersion = 1 }), HttpStatusCode.OK);
        }

        using var feed = JsonDocument.Parse(await admin.GetStringAsync($"/api/admin/access-audit?userId={adminId}&limit=2"));
        var items = feed.RootElement.GetProperty("items");
        var published = items[0];
        Assert.Equal("api.write", published.GetProperty("action").GetString());
        Assert.Equal("api", published.GetProperty("apiChannel").GetString());
        Assert.Equal("page.publish", published.GetProperty("apiOperation").GetString());
        Assert.Equal("ok", published.GetProperty("apiOutcome").GetString());
        Assert.Equal(1, published.GetProperty("apiResultVersion").GetInt32());
        Assert.Equal(tokenId.ToString(), published.GetProperty("apiTokenId").GetString());
        Assert.Equal("Claude Code – notebook", published.GetProperty("apiTokenName").GetString());
        Assert.Equal(pageId, published.GetProperty("pageId").GetString());
        Assert.Equal("Postup VPN", published.GetProperty("pageTitle").GetString());
        Assert.False(string.IsNullOrEmpty(published.GetProperty("spaceName").GetString()));
        Assert.Equal("page.create", items[1].GetProperty("apiOperation").GetString());
    }
}
