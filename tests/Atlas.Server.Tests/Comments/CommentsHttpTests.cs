using System.Net;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Xunit;

namespace Atlas.Server.Tests.Comments;

/// <summary>What HTTP adds to comments: the routes under an object, the camelCase shapes the web client codes against, Created with
/// its location, 204 on deletion and the refusal of a malformed id list.</summary>
public sealed class CommentsHttpTests
{
    [PostgreSqlFact]
    public async Task Comments_of_an_object_are_posted_read_edited_and_deleted_over_http()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_comments_http_", settings => settings["Modules:Boards:Enabled"] = "true");
        using var admin = site.CreateDefaultClient(new Uri("https://localhost"), new CookieContainerHandler(new CookieContainer()));
        await site.CompleteSetupAsync(admin);
        var spaceId = (await AtlasHttpSite.Json(await admin.GetAsync("/api/spaces"), HttpStatusCode.OK))[0]!["id"]!.GetValue<string>();
        var boardId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/boards", new { title = "Wall" }),
            HttpStatusCode.Created))["id"]!.GetValue<string>();

        var posted = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/objects/{boardId}/comments", new { body = "První **komentář**", anchor = "frame-1" });
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        var comment = await AtlasHttpSite.Json(posted, HttpStatusCode.Created);
        var id = comment["id"]!.GetValue<string>();
        Assert.Equal($"/api/objects/{boardId}/comments/{id}", posted.Headers.Location!.OriginalString);
        Assert.Equal(["id", "authorId", "authorName", "body", "anchor", "createdAt", "editedAt", "deleted", "canEdit", "canDelete"],
            comment.AsObject().Select(property => property.Key));

        var page = await AtlasHttpSite.Json(await admin.GetAsync($"/api/objects/{boardId}/comments"), HttpStatusCode.OK);
        Assert.Equal(["items", "hasMore", "count", "canPost"], page.AsObject().Select(property => property.Key));
        Assert.Equal((1, false, true), (page["count"]!.GetValue<int>(), page["hasMore"]!.GetValue<bool>(), page["canPost"]!.GetValue<bool>()));
        Assert.Equal("""{"items":[],"hasMore":false,"count":1,"canPost":true}""",
            (await AtlasHttpSite.Json(await admin.GetAsync($"/api/objects/{boardId}/comments?before={id}"), HttpStatusCode.OK)).ToJsonString());

        var edited = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/objects/{boardId}/comments/{id}", new { body = "Upraveno" }),
            HttpStatusCode.OK);
        Assert.NotNull(edited["editedAt"]);
        Assert.Equal($$"""{"count":1,"latestId":"{{id}}"}""",
            (await AtlasHttpSite.Json(await admin.GetAsync($"/api/objects/{boardId}/comments/count"), HttpStatusCode.OK)).ToJsonString());
        Assert.Equal($$$"""{"counts":{"{{{boardId}}}":1}}""",
            (await AtlasHttpSite.Json(await admin.GetAsync($"/api/objects/comments/counts?ids={boardId},{Guid.NewGuid()}"), HttpStatusCode.OK)).ToJsonString());
        Assert.Equal("invalid_comment",
            (await AtlasHttpSite.Json(await admin.GetAsync("/api/objects/comments/counts?ids=nope"), HttpStatusCode.BadRequest))["code"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Delete, $"/api/objects/{boardId}/comments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/objects/{Guid.NewGuid()}/comments")).StatusCode);
    }
}
