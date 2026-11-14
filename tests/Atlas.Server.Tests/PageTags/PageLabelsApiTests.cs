using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.PageTags;

/// <summary>
/// The page icon and tags over the personal REST API (/api/v1): the cookie routes' handlers behind a bearer token with the draft
/// scope. The change is visible at once and creates no document revision.
/// </summary>
public sealed class PageLabelsApiTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Decoration_round_trips_with_a_personal_token_and_conflicts_leave_the_document_alone()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Příručka", Rich(Paragraph("Text.")), "Text.");
        await f.AdminTokenAsync();
        using var api = await ApiAsync(f, "read", "write");
        using var reader = await ApiAsync(f, "read");

        // The catalog names the identifiers.
        var catalog = await AtlasHttpSite.Json(await api.GetAsync("/api/v1/appearance?q=book-open"), HttpStatusCode.OK);
        Assert.Contains("book-open", catalog["icons"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("blue-3", catalog["colors"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()));

        var none = await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/pages/{page.Id}/decoration"), HttpStatusCode.OK);
        Assert.Null(none["decoration"]);
        var set = await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/pages/{page.Id}/decoration",
            new { icon = "book-open", color = "blue-3", expectedRevisionId = (string?)null }), HttpStatusCode.OK);
        var revision = set["decoration"]!["revisionId"]!.GetValue<string>();
        Assert.Equal("book-open", set["decoration"]!["icon"]!.GetValue<string>());

        // A stale revision is decoration_changed; an unknown icon is 400; a read token cannot write.
        var stale = await api.PutAsJsonAsync($"/api/v1/pages/{page.Id}/decoration", new { icon = "star", color = "red-2", expectedRevisionId = (string?)null });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("decoration_changed", (await AtlasHttpSite.Json(stale, HttpStatusCode.Conflict))["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsJsonAsync($"/api/v1/pages/{page.Id}/decoration",
            new { icon = "no-such-icon", color = "blue-3", expectedRevisionId = revision })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"/api/v1/pages/{page.Id}/decoration",
            new { icon = "star", color = "red-2", expectedRevisionId = revision })).StatusCode);

        var cleared = await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/pages/{page.Id}/decoration",
            new { icon = (string?)null, color = (string?)null, expectedRevisionId = revision }), HttpStatusCode.OK);
        Assert.Null(cleared["decoration"]!["icon"]);
        Assert.Null(cleared["decoration"]!["color"]);

        // Not a document change: one revision, version 1, still published, the body untouched.
        var id = Guid.Parse(page.Id);
        Assert.Equal(1L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageRevision\" WHERE \"PageId\"=@page", ("page", id)));
        Assert.Equal(page.Version, (int)(await f.Site.ScalarAsync("SELECT \"Version\" FROM \"Page\" WHERE \"Id\"=@page", ("page", id)))!);
        Assert.Contains("Text.", (string)(await f.Site.ScalarAsync("SELECT \"ContentJson\" FROM \"PageRevision\" WHERE \"PageId\"=@page", ("page", id)))!);
    }

    [PostgreSqlFact]
    public async Task Tag_changes_keep_other_tags_and_concurrent_changes_do_not_lose_each_other()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Příručka", Rich(Paragraph("Text.")), "Text.");
        await f.AdminTokenAsync();
        using var api = await ApiAsync(f, "read", "write");
        var path = $"/api/v1/pages/{page.Id}/tags";
        // Tags are one catalog of the wiki: names of the test's own, or another test's spelling of them would come back.
        var own = Guid.NewGuid().ToString("N")[..8];

        var first = await AtlasHttpSite.Json(await api.PutAsJsonAsync(path, new { names = new[] { $"příručka {own}", $"DEMO {own}" }, expectedTagIds = Array.Empty<string>() }), HttpStatusCode.OK);
        Assert.Equal(2, first["tags"]!.AsArray().Count);
        // add/remove keep what they do not name, match names without regard to case and skip a tag the page does not carry.
        var changed = await AtlasHttpSite.Json(await api.PostAsJsonAsync(path + "/changes", new { add = new[] { $"k revizi {own}", $"PAK {own}" }, remove = new[] { $"demo {own}", "neexistuje" } }), HttpStatusCode.OK);
        Assert.Equal([$"k revizi {own}", $"PAK {own}", $"příručka {own}"], Names(changed));

        // A stale expectedTagIds is still tags_changed; the full set is replaced only with the current ids.
        var stale = await api.PostAsJsonAsync(path + "/changes", new { add = new[] { "x" }, expectedTagIds = first["tags"]!.AsArray().Select(t => t!["id"]!.GetValue<string>()).ToArray() });
        Assert.Equal("tags_changed", (await AtlasHttpSite.Json(stale, HttpStatusCode.Conflict))["code"]!.GetValue<string>());

        // Six clients add one tag each at once: every tag and the earlier ones survive.
        // A gate releases all six requests together so they contend for the page lock instead of running one after another.
        var gate = new TaskCompletionSource();
        var adds = Enumerable.Range(1, 6).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return await api.PostAsJsonAsync(path + "/changes", new { add = new[] { "souběh" + i } });
        })).ToArray();
        gate.SetResult();
        foreach (var response in await Task.WhenAll(adds))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var final = await AtlasHttpSite.Json(await api.GetAsync(path), HttpStatusCode.OK);
        Assert.Equal(9, final["tags"]!.AsArray().Count);
        Assert.Contains($"příručka {own}", Names(final));
        Assert.Equal(1L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageRevision\" WHERE \"PageId\"=@page", ("page", Guid.Parse(page.Id))));
    }

    [PostgreSqlFact]
    public async Task Suggestions_and_listing_never_reveal_the_tag_of_a_page_the_caller_cannot_read()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        // An unpublished draft of the administrator is invisible to a reader of the space.
        var secret = await PageAsync(f.Admin, space, "Skrytý koncept", Rich(Paragraph("Text.")), "Text.", publish: false);
        var visible = await PageAsync(f.Admin, space, "Veřejná", Rich(Paragraph("Text.")), "Text.");
        await f.AdminTokenAsync();
        using var admin = await ApiAsync(f, "read", "write");
        // The wiki administrator lists every test's tags: names of the test's own, which both ask for.
        var secretTag = $"tajnyštítek {Guid.NewGuid().ToString("N")[..8]}";
        var hidden = await AtlasHttpSite.Json(await admin.PostAsJsonAsync($"/api/v1/pages/{secret.Id}/tags/changes", new { add = new[] { secretTag } }), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await admin.PostAsJsonAsync($"/api/v1/pages/{visible.Id}/tags/changes", new { add = new[] { $"veřejnýštítek {Guid.NewGuid().ToString("N")[..8]}" } }), HttpStatusCode.OK);
        var tagId = hidden["tags"]![0]!["id"]!.GetValue<string>();
        var (readerCookie, readerId) = await f.UserAsync("ctenar");
        await f.GrantAsync(space, readerId, "Reader");
        var token = await IssueAsync(readerCookie, f.Site.Password, ["read"], ["rest"]);
        using var reader = f.Site.Client();
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Secret);

        // The administrator sees the tag and the page; the reader sees only the public one.
        var secretQuery = "/api/v1/tags?q=" + Uri.EscapeDataString(secretTag);
        Assert.Single((await AtlasHttpSite.Json(await admin.GetAsync(secretQuery), HttpStatusCode.OK))["items"]!.AsArray());
        Assert.Single((await AtlasHttpSite.Json(await admin.GetAsync($"/api/v1/tags/{tagId}/pages"), HttpStatusCode.OK))["items"]!.AsArray());
        Assert.Empty((await AtlasHttpSite.Json(await reader.GetAsync(secretQuery), HttpStatusCode.OK))["items"]!.AsArray());
        Assert.Empty((await AtlasHttpSite.Json(await reader.GetAsync($"/api/v1/tags/{tagId}/pages?spaceId={space}"), HttpStatusCode.OK))["items"]!.AsArray());
        Assert.Single((await AtlasHttpSite.Json(await reader.GetAsync("/api/v1/tags?q=štítek"), HttpStatusCode.OK))["items"]!.AsArray());
    }

    private static string[] Names(JsonNode state) => state["tags"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray();

    /// <summary>The administrator's token with the given scopes on the channel rest.</summary>
    private static async Task<HttpClient> ApiAsync(McpSite f, params string[] scopes)
    {
        var token = await IssueAsync(f.Admin, f.Site.Password, scopes, ["rest"]);
        var client = f.Site.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Secret);
        return client;
    }
}
