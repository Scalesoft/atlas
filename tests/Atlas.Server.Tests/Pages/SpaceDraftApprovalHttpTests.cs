using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Pages;

public sealed class SpaceDraftApprovalHttpTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";

    [PostgreSqlFact]
    public async Task Queue_includes_new_and_changed_pages_and_approval_checks_the_reviewed_revision_and_space()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_draft_approval_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        var fresh = await Create(admin, spaceId, "New guide");
        var changed = await Create(admin, spaceId, "Original guide");
        var changedId = changed["id"]!.GetValue<string>();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{changedId}/publish", new { expectedVersion = 1 }), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{changedId}", new { title = "Updated guide", contentJson = Rich, markdown = "Update", expectedVersion = 1 }), HttpStatusCode.OK);

        var queue = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/drafts"), HttpStatusCode.OK);
        Assert.Equal(2, queue["total"]!.GetValue<int>());
        var items = queue["items"]!.AsArray();
        var newItem = items.Single(x => x!["id"]!.GetValue<string>() == fresh["id"]!.GetValue<string>())!;
        var updatedItem = items.Single(x => x!["id"]!.GetValue<string>() == changedId)!;
        Assert.Null(newItem["publishedRevisionId"]);
        Assert.Equal(1, updatedItem["publishedVersion"]!.GetValue<int>());
        Assert.Equal("Administrator", updatedItem["authorName"]!.GetValue<string>());
        Assert.NotNull(updatedItem["changedAt"]);
        Assert.Null(updatedItem["markdown"]);
        Assert.Equal("Administrator", Assert.Single(queue["authors"]!.AsArray())!["name"]!.GetValue<string>());
        var filtered = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/drafts?kind=changes&q=Updated&authorId={updatedItem["authorId"]}"), HttpStatusCode.OK);
        Assert.Equal(changedId, Assert.Single(filtered["items"]!.AsArray())!["id"]!.GetValue<string>());
        var paged = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/drafts?limit=1&offset=1"), HttpStatusCode.OK);
        Assert.Equal(2, paged["total"]!.GetValue<int>());
        Assert.Single(paged["items"]!.AsArray());

        var id = newItem["id"]!.GetValue<string>();
        var revisionId = newItem["revisionId"]!.GetValue<string>();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{Guid.NewGuid()}/drafts/{id}/approve", new { expectedVersion = 1, expectedRevisionId = revisionId }), HttpStatusCode.Conflict);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/drafts/{id}/approve", new { expectedVersion = 1, expectedRevisionId = Guid.NewGuid() }), HttpStatusCode.Conflict);
        var published = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/drafts/{id}/approve", new { expectedVersion = 1, expectedRevisionId = revisionId, changeSummary = "  Schváleno správcem  " }), HttpStatusCode.OK);
        Assert.True(published["isPublished"]!.GetValue<bool>());
        var history = await AtlasHttpSite.Json(await admin.GetAsync($"/api/pages/{id}/history"), HttpStatusCode.OK);
        Assert.Equal("Schváleno správcem", Assert.Single(history["items"]!.AsArray())!["changeSummary"]!.GetValue<string>());

        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{changedId}", new { title = "Later edit", contentJson = Rich, markdown = "Later", expectedVersion = 2 }), HttpStatusCode.OK);
        var stale = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/drafts/{changedId}/approve", new { expectedVersion = 2, expectedRevisionId = updatedItem["revisionId"]!.GetValue<string>() }), HttpStatusCode.Conflict);
        Assert.Equal("draft_changed", stale["code"]!.GetValue<string>());
        var after = await AtlasHttpSite.Json(await admin.GetAsync($"/api/pages/{changedId}"), HttpStatusCode.OK);
        Assert.Equal(1, after["publishedVersion"]!.GetValue<int>());
        var remaining = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/drafts"), HttpStatusCode.OK);
        Assert.Equal(changedId, Assert.Single(remaining["items"]!.AsArray())!["id"]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task After_draft_changed_the_reloaded_row_approves_the_newer_revision_and_the_seen_one_still_conflicts()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_draft_renewed_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        var id = (await Create(admin, spaceId, "Typed guide"))["id"]!.GetValue<string>();
        await Create(admin, spaceId, "Other guide");
        var seen = Assert.Single((await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/drafts?pageId={id}&limit=1"), HttpStatusCode.OK))["items"]!.AsArray())!;
        // Autosave: someone keeps typing and saves a newer revision after the admin loaded the queue.
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{id}", new { title = "Typed guide, more", contentJson = Rich, markdown = "More", expectedVersion = 1 }), HttpStatusCode.OK);
        var old = new { expectedVersion = seen["version"]!.GetValue<int>(), expectedRevisionId = seen["revisionId"]!.GetValue<string>() };
        Assert.Equal("draft_changed", (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/drafts/{id}/approve", old), HttpStatusCode.Conflict))["code"]!.GetValue<string>());

        var reloaded = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/drafts?pageId={id}&limit=1"), HttpStatusCode.OK);
        var row = Assert.Single(reloaded["items"]!.AsArray())!;
        Assert.Equal(1, reloaded["total"]!.GetValue<int>());
        Assert.NotEqual(seen["revisionId"]!.GetValue<string>(), row["revisionId"]!.GetValue<string>());
        var published = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/drafts/{id}/approve",
            new { expectedVersion = row["version"]!.GetValue<int>(), expectedRevisionId = row["revisionId"]!.GetValue<string>() }), HttpStatusCode.OK);
        Assert.True(published["isPublished"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Conflict, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/drafts/{id}/approve", old)).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Approval_is_admin_only_preserves_author_publishing_and_excludes_other_spaces_and_trash()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_draft_guard_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        var writerLogin = TestLogin.For("writer");
        var invitation = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username = writerLogin, displayName = "Writer", isWikiAdmin = false }), HttpStatusCode.Created);
        using var writer = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, "/api/auth/activate", new { token = invitation["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, "/api/auth/login", new { username = writerLogin, password = site.Password }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access",
            new { userId = invitation["user"]!["id"]!.GetValue<string>(), role = "Admin", expectedVersion = (string?)null },
            await AtlasHttpSite.ReauthAsync(admin, site.Password))).StatusCode);
        var draft = await Create(writer, spaceId, "Writer draft");
        var id = draft["id"]!.GetValue<string>();
        var queue = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/drafts?kind=new"), HttpStatusCode.OK);
        var item = Assert.Single(queue["items"]!.AsArray())!;
        Assert.Equal("Writer", item["authorName"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.GetAsync($"/api/spaces/{spaceId}/drafts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, $"/api/spaces/{spaceId}/drafts/{id}/approve",
            new { expectedVersion = 1, expectedRevisionId = item["revisionId"]!.GetValue<string>() })).StatusCode);
        // Authors retain the original publishing capability; the review screen does not impose a new workflow.
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, $"/api/pages/{id}/publish", new { expectedVersion = 1 }), HttpStatusCode.OK);
        Assert.Empty((await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/drafts"), HttpStatusCode.OK))["items"]!.AsArray());

        var other = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/spaces", new { name = TestLogin.For("Other space") }), HttpStatusCode.OK);
        await Create(admin, other["id"]!.GetValue<string>(), "Elsewhere");
        var trash = await Create(admin, spaceId, "Removed draft");
        await site.ExecuteAsync("UPDATE \"Page\" SET \"IsTrashed\"=true,\"TrashedAt\"=now(),\"TrashedById\"=\"OwnerId\",\"TrashBatchId\"=gen_random_uuid() WHERE \"Id\"=@id", ("id", trash["id"]!.GetValue<Guid>()));
        var empty = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/drafts"), HttpStatusCode.OK);
        Assert.Equal(0, empty["total"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/drafts/{trash["id"]}/approve",
            new { expectedVersion = 1, expectedRevisionId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/spaces/{Guid.NewGuid()}/drafts")).StatusCode);
        foreach (var filter in new[] { "kind=invalid", "offset=-1", "limit=0", "limit=101", "q=" + new string('a', 201) })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/spaces/{spaceId}/drafts?{filter}")).StatusCode);
        }
    }

    private static async Task<JsonNode> Create(HttpClient admin, string spaceId, string title)
    {
        return await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title, contentJson = Rich, markdown = title }), HttpStatusCode.OK);
    }
}
