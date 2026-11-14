using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Spaces;

public sealed class SpacesHttpTests
{
    [PostgreSqlFact]
    public async Task Space_list_and_page_tree_keep_their_JSON_fields()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_spaces_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        using var member = await MemberAsync(site, admin, spaceId, "Admin");
        var space = await ListedAsync(member, spaceId);
        Assert.Equal(new[] { "id", "name", "icon", "color", "role", "description", "itemAccessOnly", "pinPolicy", "pinned", "pageCount", "lastChangedAt", "cover" }, space.AsObject().Select(property => property.Key).ToArray());
        Assert.Equal("Admin", space["role"]!.GetValue<string>());
        Assert.Equal("", space["description"]!.GetValue<string>());
        const string rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages", new { spaceId, parentId = (string?)null, title = "Tree", contentJson = rich, markdown = "t" }), HttpStatusCode.OK);
        var tree = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/pages"), HttpStatusCode.OK);
        Assert.Equal(new[] { "id", "spaceId", "parentId", "title", "version", "isPublished" }, tree[0]!.AsObject().Select(property => property.Key).ToArray());
        Assert.Equal(page["id"]!.GetValue<string>(), tree[0]!["id"]!.GetValue<string>());
        Assert.Equal("Tree", tree[0]!["title"]!.GetValue<string>());
        Assert.Equal(1, tree[0]!["version"]!.GetValue<int>());
        Assert.False(tree[0]!["isPublished"]!.GetValue<bool>());
        Assert.Null(tree[0]!["parentId"]);
    }

    [PostgreSqlFact]
    public async Task Pin_route_binds_the_choice_and_refuses_unpinning_a_required_space_with_its_code()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_space_pin_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        // The starter space of an installation has this policy; a space made later has none.
        await site.ExecuteAsync("UPDATE \"Space\" SET \"PinPolicy\"='Default' WHERE \"Id\"=@id", ("id", Guid.Parse(spaceId)));
        using var member = await MemberAsync(site, admin, spaceId, "Reader");
        var space = await ListedAsync(member, spaceId);
        var path = $"/api/me/spaces/{spaceId}/pin";
        // The starter space is pinned for everyone; the reader's own unpin wins over Default.
        Assert.Equal("Default", space["pinPolicy"]!.GetValue<string>());
        Assert.False((await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path, new { pinned = false }), HttpStatusCode.OK))["pinned"]!.GetValue<bool>());
        await site.ExecuteAsync("UPDATE \"Space\" SET \"PinPolicy\"='Required' WHERE \"Id\"=@id", ("id", Guid.Parse(spaceId)));
        var refused = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path, new { pinned = false }), HttpStatusCode.Conflict);
        Assert.Equal("space_pin_required", refused["code"]!.GetValue<string>());
    }

    /// <summary>A signed-in account whose only grant is <paramref name="role"/> in the space. A wiki administrator lists every space of
    /// the shared site, only the first 200 by name, so a test reads its own space from the list as such a member.</summary>
    internal static async Task<HttpClient> MemberAsync(AtlasHttpSite site, HttpClient admin, string spaceId, string role)
    {
        var (userId, member) = await ApiActivity.ApiActivityTests.InviteAsync(site, admin, "member");
        // Granting Admin needs a fresh sign-in (owner decision 3a).
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access",
            new { userId, role, expectedVersion = (string?)null }, role == "Admin" ? await AtlasHttpSite.ReauthAsync(admin, site.Password) : null)).StatusCode);
        return member;
    }

    /// <summary>The space in the client's list of spaces.</summary>
    internal static async Task<JsonNode> ListedAsync(HttpClient client, string spaceId)
        => Assert.Single((await AtlasHttpSite.Json(await client.GetAsync("/api/spaces"), HttpStatusCode.OK)).AsArray(), x => x!["id"]!.GetValue<string>() == spaceId)!;
}
