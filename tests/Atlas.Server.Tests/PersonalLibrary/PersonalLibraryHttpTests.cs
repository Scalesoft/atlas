using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.ApiActivity;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PersonalLibrary;

public sealed class PersonalLibraryHttpTests
{
    [PostgreSqlFact]
    public async Task Favorite_visit_state_and_lists_round_trip_over_http()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_library_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        const string rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Kept", contentJson = rich, markdown = "" }), HttpStatusCode.OK);
        var id = page["id"]!.GetValue<string>();

        var favorite = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{id}/favorite", new { isFavorite = true }), HttpStatusCode.OK);
        Assert.True(favorite["isFavorite"]!.GetValue<bool>());
        var visited = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{id}/visit", new { }), HttpStatusCode.OK);
        Assert.NotNull(visited["visitedAt"]);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Europe/Prague"));
        var period = $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}";
        var activity = await AtlasHttpSite.Json(await admin.GetAsync($"/api/analytics/activity?{period}&scope=mine"), HttpStatusCode.OK);
        Assert.Equal(1, activity["revisions"]!.GetValue<int>());
        Assert.Equal(1, activity["pages"]!.GetValue<int>());
        Assert.Equal(1, activity["authors"]![0]!["revisions"]!.GetValue<int>());
        Assert.Equal(1, activity["days"]![0]!["count"]!.GetValue<int>());
        var changes = await AtlasHttpSite.Json(await admin.GetAsync($"/api/analytics/changes?day={today:yyyy-MM-dd}&scope=mine"), HttpStatusCode.OK);
        Assert.Equal(id, changes["items"]![0]!["pageId"]!.GetValue<string>());
        var readership = await AtlasHttpSite.Json(await admin.GetAsync($"/api/analytics/pages/{id}?{period}"), HttpStatusCode.OK);
        Assert.Equal(1, readership["views"]!.GetValue<int>());
        Assert.Equal(1, readership["readers"]!.GetValue<int>());
        var state = await AtlasHttpSite.Json(await admin.GetAsync($"/api/pages/{id}/personal-state"), HttpStatusCode.OK);
        Assert.True(state["isFavorite"]!.GetValue<bool>());

        var favorites = await AtlasHttpSite.Json(await admin.GetAsync("/api/me/library/favorites"), HttpStatusCode.OK);
        Assert.Equal(id, favorites["items"]![0]!["id"]!.GetValue<string>());
        var recent = await AtlasHttpSite.Json(await admin.GetAsync("/api/me/library/recent?offset=0&limit=20"), HttpStatusCode.OK);
        Assert.Single(recent["items"]!.AsArray());
        Assert.False(recent["hasMore"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/me/library/unknown")).StatusCode);

        // A wiki administrator's popular list ranks every test's pages (its top 20): a reader of the one space sees the page's view.
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{id}/publish",
            new { expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        var (readerId, reader) = await ApiActivityTests.InviteAsync(site, admin, "reader");
        using (reader)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access",
                new { userId = readerId, role = "Reader", expectedVersion = (string?)null })).StatusCode);
            var popular = await AtlasHttpSite.Json(await reader.GetAsync($"/api/analytics/popular?{period}"), HttpStatusCode.OK);
            Assert.Equal(id, Assert.Single(popular["items"]!.AsArray())!["pageId"]!.GetValue<string>());
        }
    }
}
