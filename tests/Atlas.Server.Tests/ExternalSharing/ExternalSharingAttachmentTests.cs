using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.ExternalSharing;

public sealed class ExternalSharingAttachmentTests
{
    [PostgreSqlFact]
    public async Task Public_attachment_access_is_bound_to_fixed_revision_and_rechecked_for_HEAD_after_revoke()
    {
        await using var fixture = await SharingSite.CreateAsync();
        await fixture.SetPolicy(true, null);
        var file = await Upload(fixture, "published bytes", "shared.txt");
        var other = await Upload(fixture, "draft bytes", "draft.txt");
        var canonical = file["downloadUrl"]!.GetValue<string>();
        var document = JsonNode.Parse(SharingSite.Rich("Download"))!;
        document["document"]!["content"]![0]!["content"]![0]!["marks"] = new JsonArray(new JsonObject
            { ["type"] = "link", ["attrs"] = new JsonObject { ["href"] = canonical } });
        var page = await AtlasHttpSite.Json(await fixture.Admin.GetAsync(fixture.PagePath), HttpStatusCode.OK);
        page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(fixture.Admin, HttpMethod.Put, fixture.PagePath,
            new { title = "With attachment", contentJson = document.ToJsonString(), markdown = "[Download](" + canonical + ")", expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(fixture.Admin, HttpMethod.Post, fixture.PagePath + "/publish",
            new { expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        var issue = await AtlasHttpSite.Json(await fixture.Issue(), HttpStatusCode.Created);
        var prefix = "/api/shared/" + issue["url"]!.GetValue<string>()[3..];
        var shared = await AtlasHttpSite.Json(await fixture.Anonymous.GetAsync(prefix), HttpStatusCode.OK);
        var download = prefix + "/attachments/" + file["id"]!.GetValue<string>() + "/download";
        Assert.Contains(download, shared["contentJson"]!.GetValue<string>());
        Assert.Null(shared["markdown"]);
        var response = await fixture.Anonymous.GetAsync(download);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("published bytes", await response.Content.ReadAsStringAsync());
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Anonymous.GetAsync(prefix + "/attachments/" + other["id"]!.GetValue<string>() + "/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Anonymous.GetAsync(download.Replace("/download", "/content", StringComparison.Ordinal))).StatusCode);
        await AtlasHttpSite.SendAsync(fixture.Admin, HttpMethod.Delete, fixture.LinksPath + "/" + issue["link"]!["id"]!.GetValue<string>());
        using var head = new HttpRequestMessage(HttpMethod.Head, download);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Anonymous.SendAsync(head)).StatusCode);
    }

    private static async Task<JsonNode> Upload(SharingSite fixture, string text, string name)
    {
        var csrf = await AtlasHttpSite.Json(await fixture.Admin.GetAsync("/api/auth/csrf"), HttpStatusCode.OK);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(text), "file", name);
        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.PagePath + "/attachments") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        return await AtlasHttpSite.Json(await fixture.Admin.SendAsync(request), HttpStatusCode.Created);
    }
}
