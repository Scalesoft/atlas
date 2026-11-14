using System.Net;
using System.Net.Http.Json;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PageAccess;

/// <summary>WP3 S2 contract over HTTP: a space Reader who can read the page is told why a page operation is refused (403 with a
/// problem detail the web client shows), where 1.0 answered 404; an account that cannot read the page still gets 404.</summary>
public sealed class ReaderRefusalHttpTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Page"}]}]}}""";

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task A_reader_is_told_403_for_publish_rename_upload_and_access_history()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_reader_refusal_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(admin));
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (Guid?)null, title = "Page", contentJson = Rich, markdown = "Page" }), HttpStatusCode.OK);
        var pageId = page["id"]!.GetValue<Guid>();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{pageId}/publish", new { expectedVersion = 1 }),
            HttpStatusCode.OK);

        var (reader, readerId) = await SignInAsync(site, admin, "reader");
        var (outsider, _) = await SignInAsync(site, admin, "outsider");
        using var readerClient = reader;
        using var outsiderClient = outsider;
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access",
            new { userId = readerId, role = "Reader", expectedVersion = (string?)null })).StatusCode);
        Assert.False((await AtlasHttpSite.Json(await reader.GetAsync($"/api/pages/{pageId}"), HttpStatusCode.OK))["canEdit"]!.GetValue<bool>());

        foreach (var client in new[] { reader, outsider })
        {
            var expected = client == reader ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound;
            await AssertRefusedAsync(expected, await AtlasHttpSite.SendAsync(client, HttpMethod.Post, $"/api/pages/{pageId}/publish",
                new { expectedVersion = 1 }));
            await AssertRefusedAsync(expected, await AtlasHttpSite.SendAsync(client, HttpMethod.Put, $"/api/pages/{pageId}/title",
                new { title = "Renamed", expectedVersion = 1 }));
            await AssertRefusedAsync(expected, await UploadAsync(client, pageId));
            await AssertRefusedAsync(expected, await client.GetAsync($"/api/pages/{pageId}/access-history"));
        }
        // The page is untouched.
        var unchanged = await AtlasHttpSite.Json(await admin.GetAsync($"/api/pages/{pageId}"), HttpStatusCode.OK);
        Assert.Equal("Page", unchanged["title"]!.GetValue<string>());
        Assert.Equal(1, unchanged["version"]!.GetValue<int>());
    }

    private static async Task AssertRefusedAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        var problem = await AtlasHttpSite.Json(response, expected);
        Assert.False(string.IsNullOrWhiteSpace(problem["detail"]?.GetValue<string>() ?? problem["title"]?.GetValue<string>()));
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid pageId)
    {
        var csrf = await AtlasHttpSite.Json(await client.GetAsync("/api/auth/csrf"), HttpStatusCode.OK);
        var form = new MultipartFormDataContent { { new ByteArrayContent("note"u8.ToArray()), "file", "note.txt" } };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/pages/{pageId}/attachments") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        return await client.SendAsync(request);
    }

    private static async Task<(HttpClient Client, Guid Id)> SignInAsync(AtlasHttpSite site, HttpClient admin, string username)
    {
        username = TestLogin.For(username);
        var invitation = (await (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username, displayName = username, isWikiAdmin = false })).Content.ReadFromJsonAsync<CreatedBuiltinUser>())!;
        var client = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/activate",
            new { token = invitation.Token, newPassword = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/login",
            new { username, password = site.Password })).StatusCode);
        return (client, invitation.User.Id);
    }
}
