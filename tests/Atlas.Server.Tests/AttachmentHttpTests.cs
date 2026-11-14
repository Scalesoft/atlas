using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests;
public sealed class AttachmentHttpTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Multipart_cookie_and_bearer_upload_enforce_CSRF_scope_and_private_safe_range_download()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_attachment_http_");
        using var admin = site.Client();
        var user = await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        const string rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var page = await Json(await Send(admin, HttpMethod.Post, "/api/pages", new { spaceId, parentId = (string?)null, title = "Files", contentJson = rich, markdown = "" }), HttpStatusCode.OK);
        var path = $"/api/pages/{page["id"]!.GetValue<string>()}/attachments";
        using (var bad = Multipart("<html>payload</html>"u8.ToArray(), "text.html")) Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync(path, bad)).StatusCode);
        var csrf = await Json(await admin.GetAsync("/api/auth/csrf"), HttpStatusCode.OK);
        using var upload = new HttpRequestMessage(HttpMethod.Post, path) { Content = Multipart(new byte[6 * 1024 * 1024], "../../report.html") };
        upload.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        var file = await Json(await admin.SendAsync(upload), HttpStatusCode.Created);
        using var invalidImage = new HttpRequestMessage(HttpMethod.Post, path) { Content = Multipart("<svg onload='alert(1)'/>"u8.ToArray(), "looks-like.png") };
        ((MultipartFormDataContent)invalidImage.Content).Add(new StringContent("image"), "kind");
        invalidImage.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(invalidImage)).StatusCode);
        using var validImage = new HttpRequestMessage(HttpMethod.Post, path) { Content = Multipart(Pixel, "pixel.png") };
        ((MultipartFormDataContent)validImage.Content).Add(new StringContent("image"), "kind");
        validImage.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        var image = await Json(await admin.SendAsync(validImage), HttpStatusCode.Created);
        var inline = await admin.GetAsync(image["url"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, inline.StatusCode);
        Assert.Equal("image/png", inline.Content.Headers.ContentType!.MediaType);
        Assert.Null(inline.Content.Headers.ContentDisposition);
        Assert.Equal(6 * 1024 * 1024, file["size"]!.GetValue<int>());
        Assert.Equal("report.html", file["name"]!.GetValue<string>());
        var url = file["downloadUrl"]!.GetValue<string>();
        using var anonymous = site.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 9);
        var download = await admin.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, download.StatusCode);
        Assert.Equal(10, (await download.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(download.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        var metadata = await admin.SendAsync(head);
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.Equal(6 * 1024 * 1024, metadata.Content.Headers.ContentLength);
        Assert.Empty(await metadata.Content.ReadAsByteArrayAsync());
        await Json(await Send(admin, HttpMethod.Put, $"/api/admin/users/{user["id"]!.GetValue<string>()}/api-access", new { canIssueTokens = true, expectedVersion = (string?)null }), HttpStatusCode.OK);
        await AtlasHttpSite.ReauthAsync(admin, site.Password);
        var read = await Json(await Send(admin, HttpMethod.Post, "/api/profile/api-tokens", new { name = "Read", scopes = new[] { "read" }, expiresInDays = 1 }), HttpStatusCode.Created);
        using var api = site.Client();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", read["secret"]!.GetValue<string>());
        var apiPath = path.Replace("/api/", "/api/v1/", StringComparison.Ordinal);
        var description = await Json(await api.GetAsync("/api/v1/openapi.json"), HttpStatusCode.OK);
        Assert.NotNull(description["paths"]!["/api/v1/pages/{pageId}/attachments"]!["post"]!["requestBody"]!["content"]!["multipart/form-data"]);
        Assert.NotNull(description["paths"]!["/api/v1/attachments/{id}/download"]!["get"]!["responses"]!["200"]!["content"]!["application/octet-stream"]);
        Assert.NotNull(description["paths"]!["/api/v1/attachments/{id}/download"]!["get"]!["responses"]!["206"]!["content"]!["application/octet-stream"]);
        foreach (var mime in new[] { "image/png", "image/jpeg", "image/gif", "image/webp" })
            Assert.NotNull(description["paths"]!["/api/v1/attachments/{id}/content"]!["get"]!["responses"]!["200"]!["content"]![mime]);
        using (var denied = Multipart("read cannot write"u8.ToArray(), "denied.txt")) Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsync(apiPath, denied)).StatusCode);
        var write = await Json(await Send(admin, HttpMethod.Post, "/api/profile/api-tokens", new { name = "Write", scopes = new[] { "read", "write" }, expiresInDays = 1 }), HttpStatusCode.Created);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", write["secret"]!.GetValue<string>());
        using var allowed = Multipart("API payload"u8.ToArray(), "api.txt");
        var apiFile = await Json(await api.PostAsync(apiPath, allowed), HttpStatusCode.Created);
        Assert.StartsWith("/api/v1/attachments/", apiFile["downloadUrl"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync(apiFile["downloadUrl"]!.GetValue<string>())).StatusCode);
        // Only the browser's inline image is kept for revalidation; the same image over the personal API is not stored.
        var apiImage = await api.GetAsync(image["url"]!.GetValue<string>().Replace("/api/", "/api/v1/", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, apiImage.StatusCode);
        Assert.True(apiImage.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync(apiFile["url"]!.GetValue<string>())).StatusCode);
    }
    /// <summary>
    /// The browser may keep an article's image, but shows it again only after the server has authorized the reader once more:
    /// private, no-cache with the content hash as the validator. Every use is a request, so removed access ends it.
    /// </summary>
    [PostgreSqlFact]
    public async Task Inline_image_is_revalidated_on_every_use_and_removed_access_ends_it()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_attachment_cache_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        const string empty = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var page = await Json(await Send(admin, HttpMethod.Post, "/api/pages", new { spaceId, parentId = (string?)null, title = "Images", contentJson = empty, markdown = "" }), HttpStatusCode.OK);
        var pageId = page["id"]!.GetValue<string>();
        var csrf = await Json(await admin.GetAsync("/api/auth/csrf"), HttpStatusCode.OK);
        using var upload = new HttpRequestMessage(HttpMethod.Post, $"/api/pages/{pageId}/attachments") { Content = Multipart(Pixel, "pixel.png") };
        ((MultipartFormDataContent)upload.Content).Add(new StringContent("image"), "kind");
        upload.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        var url = (await Json(await admin.SendAsync(upload), HttpStatusCode.Created))["url"]!.GetValue<string>();
        // A reader may open only what the published revision references.
        var content = JsonSerializer.Serialize(new { format = "atlas-rich", version = 1,
            document = new { type = "doc", content = new[] { new { type = "image", attrs = new { src = url } } } } });
        var saved = await Json(await Send(admin, HttpMethod.Put, $"/api/pages/{pageId}", new { title = "Images", contentJson = content, markdown = "", expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        await Json(await Send(admin, HttpMethod.Post, $"/api/pages/{pageId}/publish", new { expectedVersion = saved["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        var readerLogin = TestLogin.For("reader");
        var invitation = await Json(await Send(admin, HttpMethod.Post, "/api/admin/users", new { username = readerLogin, displayName = "Reader", isWikiAdmin = false }), HttpStatusCode.Created);
        var readerId = invitation["user"]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access", new { userId = readerId, role = "Reader", expectedVersion = (string?)null })).StatusCode);
        using var reader = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(reader, HttpMethod.Post, "/api/auth/activate", new { token = invitation["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        await Json(await Send(reader, HttpMethod.Post, "/api/auth/login", new { username = readerLogin, password = site.Password }), HttpStatusCode.OK);

        using var first = await reader.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(Pixel, await first.Content.ReadAsByteArrayAsync());
        AssertStoredForRevalidationOnly(first);
        var validator = first.Headers.ETag!;
        Assert.False(validator.IsWeak);

        async Task<HttpResponseMessage> Revalidate(HttpClient client, EntityTagHeaderValue known)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.IfNoneMatch.Add(known);
            return await client.SendAsync(request);
        }

        using var unchanged = await Revalidate(reader, validator);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
        Assert.Equal(validator, unchanged.Headers.ETag);
        AssertStoredForRevalidationOnly(unchanged);
        // One authorization per request: the session lookup and a single pass of OpenAttachment (two passes took 33 statements).
        var statements = AtlasServerTiming.Statements(string.Join(",", unchanged.Headers.GetValues("Server-Timing")));
        Assert.True(statements <= RevalidationBudget, $"Revalidating an image took {statements} SQL statements; the budget is {RevalidationBudget}.");
        using var stale = await Revalidate(reader, new EntityTagHeaderValue("\"another-version\""));
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Equal(Pixel, await stale.Content.ReadAsByteArrayAsync());

        var access = $"/api/spaces/{spaceId}/access";
        var grant = Assert.Single((await Json(await admin.GetAsync(access), HttpStatusCode.OK))["grants"]!.AsArray(), x => x!["userId"]?.GetValue<string>() == readerId);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(admin, HttpMethod.Put, access, new { userId = readerId, role = (string?)null, expectedVersion = grant!["version"]!.GetValue<string>() })).StatusCode);
        // The copy in the browser is useless now: the same 404 as for an unknown attachment instead of a 304, and it is not stored.
        using var removed = await Revalidate(reader, validator);
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.True(removed.Headers.CacheControl!.NoStore);
    }

    [PostgreSqlFact]
    public async Task Malformed_multipart_shape_is_rejected_before_page_authorization_even_without_write_access()
    {
        // NOTE: a request whose Content-Type is not "multipart/form-data" never reaches this endpoint at all -
        // ASP.NET Core's routing (ConsumesMatcherPolicy, driven by .Accepts<AttachmentUploadForm>("multipart/form-data"))
        // answers 404 before any handler code runs, in both the legacy endpoint and this one. So the endpoint's own
        // "!HasFormContentType -> 415" check is unreachable over HTTP; only the in-lambda 400/400/413 shape checks
        // are observable, and this test proves those run before AuthorizeAttachmentUpload's 404.
        await using var site = await AtlasHttpSite.CreateAsync("atlas_attachment_shape_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        const string rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var page = await Json(await Send(admin, HttpMethod.Post, "/api/pages", new { spaceId, parentId = (string?)null, title = "Files", contentJson = rich, markdown = "" }), HttpStatusCode.OK);
        var path = $"/api/pages/{page["id"]!.GetValue<string>()}/attachments";
        // Invited but not granted any role in the space: AuthorizeAttachmentUpload alone would answer 404.
        var outsiderLogin = TestLogin.For("outsider");
        var invited = await Json(await Send(admin, HttpMethod.Post, "/api/admin/users", new { username = outsiderLogin, displayName = "Outsider" }), HttpStatusCode.Created);
        using var outsider = site.Client();
        await Send(outsider, HttpMethod.Post, "/api/auth/activate", new { token = invited["token"]!.GetValue<string>(), newPassword = site.Password });
        await Send(outsider, HttpMethod.Post, "/api/auth/login", new { username = outsiderLogin, password = site.Password });
        var csrf = await Json(await outsider.GetAsync("/api/auth/csrf"), HttpStatusCode.OK);
        using var invalidKind = Multipart("payload"u8.ToArray(), "file.txt");
        invalidKind.Add(new StringContent("not-a-kind"), "kind");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = invalidKind };
        request.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        var response = await outsider.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The list and the metadata over HTTP: canonical links as stored in documents, plus the personal API's bearer download.
    /// Who sees what is the catalog's rule (AttachmentHandlerTests).</summary>
    [PostgreSqlFact]
    public async Task Personal_api_lists_attachments_with_a_bearer_download_link()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_attachment_list_");
        using var admin = site.Client();
        var setup = await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        const string empty = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var pageId = (await Json(await Send(admin, HttpMethod.Post, "/api/pages", new { spaceId, parentId = (string?)null, title = "Files", contentJson = empty, markdown = "" }), HttpStatusCode.OK))["id"]!.GetValue<string>();
        var csrf = await Json(await admin.GetAsync("/api/auth/csrf"), HttpStatusCode.OK);
        using var upload = new HttpRequestMessage(HttpMethod.Post, $"/api/pages/{pageId}/attachments") { Content = Multipart("manual"u8.ToArray(), "manual.pdf") };
        upload.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        var id = (await Json(await admin.SendAsync(upload), HttpStatusCode.Created))["id"]!.GetValue<string>();
        await Json(await Send(admin, HttpMethod.Put, $"/api/admin/users/{setup["id"]!.GetValue<string>()}/api-access", new { level = "read", expectedVersion = (string?)null }), HttpStatusCode.OK);
        await AtlasHttpSite.ReauthAsync(admin, site.Password);
        var token = await Json(await Send(admin, HttpMethod.Post, "/api/profile/api-tokens", new { name = "Read", scopes = new[] { "read" }, expiresInDays = 1 }), HttpStatusCode.Created);
        using var api = site.Client();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token["secret"]!.GetValue<string>());

        var item = Assert.Single((await Json(await api.GetAsync($"/api/v1/pages/{pageId}/attachments"), HttpStatusCode.OK))["items"]!.AsArray())!;
        Assert.Equal(id, item["id"]!.GetValue<string>());
        Assert.Equal($"/api/attachments/{id}/download", item["downloadUrl"]!.GetValue<string>());
        Assert.Equal($"/api/v1/attachments/{id}/download", item["apiDownloadUrl"]!.GetValue<string>());
        Assert.Equal("manual"u8.ToArray(), await api.GetByteArrayAsync(item["apiDownloadUrl"]!.GetValue<string>()));
        var info = await Json(await api.GetAsync($"/api/v1/attachments/{id}"), HttpStatusCode.OK);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("manual"u8)), info["sha256"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/v1/attachments/{Guid.NewGuid()}")).StatusCode);
        // The cookie routes keep the canonical links only.
        var cookie = Assert.Single((await Json(await admin.GetAsync($"/api/pages/{pageId}/attachments"), HttpStatusCode.OK))["items"]!.AsArray())!;
        Assert.Null(cookie["apiDownloadUrl"]);
        var description = await Json(await api.GetAsync("/api/v1/openapi.json"), HttpStatusCode.OK);
        Assert.Equal("ListPageAttachments", description["paths"]!["/api/v1/pages/{pageId}/attachments"]!["get"]!["operationId"]!.GetValue<string>());
        Assert.Equal("GetAttachment", description["paths"]!["/api/v1/attachments/{id}"]!["get"]!["operationId"]!.GetValue<string>());
    }

    private static readonly byte[] Pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6Cj8AAAAASUVORK5CYII=");

    // Today's count for a reader (1 session lookup + 16 of OpenAttachment), +1 for a physical connection opened on a cold pool
    // (PerformanceBudgetHttpTests).
    private const int RevalidationBudget = 18;

    /// <summary>private, no-cache: only the reader's own browser keeps the image, and never shows it without asking the server.</summary>
    private static void AssertStoredForRevalidationOnly(HttpResponseMessage response)
    {
        var policy = response.Headers.CacheControl!;
        Assert.True(policy.Private);
        Assert.True(policy.NoCache);
        Assert.False(policy.NoStore);
        Assert.False(policy.Public);
        Assert.Null(policy.MaxAge);
    }

    private static MultipartFormDataContent Multipart(byte[] bytes, string name)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", name); return form;
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? payload = null) => AtlasHttpSite.SendAsync(client, method, path, payload);
    private static Task<JsonNode> Json(HttpResponseMessage response, HttpStatusCode expected) => AtlasHttpSite.Json(response, expected);
}
