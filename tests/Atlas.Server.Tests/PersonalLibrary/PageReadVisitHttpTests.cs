using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PersonalLibrary;

/// <summary>
/// The page read that opens a page records its visit (3.6-G N7, <c>GET /api/pages/{id}?visit=true</c>): once per opening, never for a
/// plain read or a read ahead (<c>?bundle=true</c> on hover), and only for someone who may read the page, as <c>POST …/visit</c> does.
/// With the bundle the recorded visit is the answer's personal state.
/// </summary>
public sealed class PageReadVisitHttpTests
{
    [PostgreSqlFact]
    public async Task Opening_a_page_records_its_visit_in_the_read_once_and_only_for_a_reader_of_it()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_readvisit_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(admin));
        var created = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (Guid?)null, title = "Navštívená", contentJson = Paragraph, markdown = "Text stránky." }),
            HttpStatusCode.OK);
        var pageId = created["id"]!.GetValue<Guid>();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{pageId}/publish",
            new { expectedVersion = created["version"]!.GetValue<int>() }), HttpStatusCode.OK);

        var (reader, readerId) = await AccountAsync(site, admin, "reader", spaceId);
        var (outsider, outsiderId) = await AccountAsync(site, admin, "outsider", spaceId: null);
        using var _ = reader;
        using var __ = outsider;
        async Task<long> Visits(Guid userId) => (long)(await site.ScalarAsync(
            "select count(*) from \"PageVisit\" where \"UserId\" = @user and \"PageId\" = @page", ("user", userId), ("page", pageId)))!;
        async Task<DateTime?> LastVisit(Guid userId) => await site.ScalarAsync(
            "select \"LastVisitedAt\" from \"PageVisit\" where \"UserId\" = @user and \"PageId\" = @page", ("user", userId), ("page", pageId)) as DateTime?;

        // A plain read and a read ahead (the bundle a hover asks for) open nothing.
        await AtlasHttpSite.Json(await reader.GetAsync($"/api/pages/{pageId}"), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await reader.GetAsync($"/api/pages/{pageId}?bundle=true"), HttpStatusCode.OK);
        Assert.Equal(0, await Visits(readerId));

        // The opening records it in the same read; the page itself is answered as by a plain read.
        var opened = await AtlasHttpSite.Json(await reader.GetAsync($"/api/pages/{pageId}?visit=true"), HttpStatusCode.OK);
        Assert.Equal(pageId, opened["id"]!.GetValue<Guid>());
        Assert.Null(opened["bundle"]);
        Assert.Equal(1, await Visits(readerId));
        var first = await LastVisit(readerId);
        Assert.NotNull(first);

        // Reads that open nothing leave the visit as it was.
        await AtlasHttpSite.Json(await reader.GetAsync($"/api/pages/{pageId}?bundle=true"), HttpStatusCode.OK);
        Assert.Equal(first, await LastVisit(readerId));

        // Another opening is the same one visit of the page, moved to now; with the bundle it is the answer's personal state.
        var again = await AtlasHttpSite.Json(await reader.GetAsync($"/api/pages/{pageId}?bundle=true&visit=true"), HttpStatusCode.OK);
        Assert.Equal(1, await Visits(readerId));
        var second = await LastVisit(readerId);
        Assert.True(second >= first);
        Assert.NotNull(again["bundle"]!["personalState"]!["visitedAt"]);

        // Someone who may not read the page gets no page and no visit, as with POST …/visit.
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/pages/{pageId}?visit=true")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(outsider, HttpMethod.Post, $"/api/pages/{pageId}/visit")).StatusCode);
        Assert.Equal(0, await Visits(outsiderId));
    }

    private static readonly string Paragraph = new JsonObject
    {
        ["format"] = "atlas-rich", ["version"] = 1,
        ["document"] = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "paragraph", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Text stránky." }),
        }) },
    }.ToJsonString();

    /// <summary>A signed-in account, a Reader of `spaceId` when given (otherwise without access to it).</summary>
    private static async Task<(HttpClient Client, Guid Id)> AccountAsync(AtlasHttpSite site, HttpClient admin, string username, Guid? spaceId)
    {
        var login = TestLogin.For(username);
        var invitation = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username = login, displayName = username, isWikiAdmin = false }), HttpStatusCode.Created);
        var id = invitation["user"]!["id"]!.GetValue<Guid>();
        if (spaceId is { } space)
            Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{space}/access",
                new { userId = id, role = "Reader", expectedVersion = (string?)null })).StatusCode);
        var client = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/activate",
            new { token = invitation["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/login",
            new { username = login, password = site.Password }), HttpStatusCode.OK);
        return (client, id);
    }
}
