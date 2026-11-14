using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests;

/// <summary>
/// Exact SQL round-trips of the three downloads whose endpoint authorizes the request itself and hands that result to
/// RevalidatingDownload as its first lease: an image of a page, an attachment of a shared link and a content export. The browser
/// may keep an image of a page but has to ask before every use (private, no-cache), and the request is authorized whether it is
/// answered 200 or 304 (AttachmentHttpTests), so every image of every page view pays this number. A count that grows by the
/// statements of the authorizing message means the transfer proves the same right again before the first byte; one that loses
/// them means nothing proves it before the first byte. RevalidatingDownloadTests holds the times of the checks that follow.
/// </summary>
public sealed class RevalidatingDownloadHttpTests
{
    [PostgreSqlFact]
    public async Task Image_shared_attachment_and_export_downloads_authorize_once_before_the_first_byte()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_download_once_", sharedAs: "revalidating-download"); // a site of this class: the next line changes how its TestServer runs every request
        site.Server.PreserveExecutionContext = true;
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(admin));
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (Guid?)null, title = "Downloads", contentJson = Rich(null), markdown = "Image" }), HttpStatusCode.OK);
        var pagePath = $"/api/pages/{page["id"]!.GetValue<Guid>()}";
        var csrf = await AtlasHttpSite.Json(await admin.GetAsync("/api/auth/csrf"), HttpStatusCode.OK);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6Cj8AAAAASUVORK5CYII=")), "file", "pixel.png");
        form.Add(new StringContent("image"), "kind");
        using var upload = new HttpRequestMessage(HttpMethod.Post, pagePath + "/attachments") { Content = form };
        upload.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        var image = await AtlasHttpSite.Json(await admin.SendAsync(upload), HttpStatusCode.Created);
        var imagePath = image["url"]!.GetValue<string>();
        // A reader gets an attachment only through the published revision that refers to it.
        page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, pagePath,
            new { title = "Downloads", contentJson = Rich(imagePath), markdown = $"[Image]({imagePath})", expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, pagePath + "/publish",
            new { expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/external-sharing",
            new { enabled = true, expectedVersion = (string?)null }), HttpStatusCode.OK);
        var link = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, pagePath + "/share-links",
            new { name = "Client review", expiresAt = DateTime.UtcNow.AddDays(1) }), HttpStatusCode.Created);
        var sharedPath = $"/api/shared/{link["url"]!.GetValue<string>()[3..]}/attachments/{image["id"]!.GetValue<string>()}/content";

        var readerLogin = TestLogin.For("reader");
        var invitation = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username = readerLogin, displayName = "Reader", isWikiAdmin = false }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access",
            new { userId = invitation["user"]!["id"]!.GetValue<Guid>(), role = "Reader", expectedVersion = (string?)null })).StatusCode);
        using var reader = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, "/api/auth/activate",
            new { token = invitation["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, "/api/auth/login",
            new { username = readerLogin, password = site.Password }), HttpStatusCode.OK);
        using var anonymous = site.Client();
        // The warm-up: each download once before it is counted.
        await GetAsync(reader, imagePath);
        await GetAsync(anonymous, sharedPath);
        await GetAsync(reader, pagePath + "/export");

        (string Download, int Expected, IReadOnlyList<string> Sent)[] counted =
        [
            // The session lookup of the cookie and OpenAttachment (16); 33 while the transfer repeated the message at its start.
            ("Image of a page by a reader", 16, await GetAsync(reader, imagePath)),
            // OpenSharedAttachment (15), no session; 30 before.
            ("Attachment of a shared link", 15, await GetAsync(anonymous, sharedPath)),
            // The session lookup, BuildContentExport (11: its page decision reads the wiki-administrator status once since bf574e825)
            // and RevalidateContentExport (9); 31 before.
            ("Export of a page by a reader", 21, await GetAsync(reader, pagePath + "/export")),
        ];

        // A changed count is diagnosed from the statements, so a failure prints them, one per line in the order they were sent.
        var differences = counted.Where(x => x.Sent.Count != x.Expected).Select(x =>
            $"{x.Download}: {x.Sent.Count} statements, expected {x.Expected}{Environment.NewLine}"
            + string.Join(Environment.NewLine, x.Sent.Select((sql, index) => $"  {index + 1}. {string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}")));
        Assert.True(counted.All(x => x.Sent.Count == x.Expected), string.Join(Environment.NewLine + Environment.NewLine, differences));
    }

    /// <summary>The SQL statements the host sent for one complete download, authentication of the request included.</summary>
    private static async Task<IReadOnlyList<string>> GetAsync(HttpClient client, string path)
    {
        using var recording = SqlRecording.Start();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
        return recording.Statements;
    }

    private static string Rich(string? href)
    {
        var text = new JsonObject { ["type"] = "text", ["text"] = "Image" };
        if (href is not null)
        {
            text["marks"] = new JsonArray(new JsonObject { ["type"] = "link", ["attrs"] = new JsonObject { ["href"] = href } });
        }
        return new JsonObject
        {
            ["format"] = "atlas-rich",
            ["version"] = 1,
            ["document"] = new JsonObject
            {
                ["type"] = "doc",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(text) })
            }
        }.ToJsonString();
    }
}
