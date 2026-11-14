using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Mcp;

/// <summary>
/// Attachments in the working revision through the C# SDK client: images and download links written as Markdown by the section tools
/// (checked against the page in the write), their look changed and the picture replaced "on the side" by atlas_update_embed, CAS, the
/// live-edit rule and nothing published. The conversion and preservation rules belong to the seam (RichEmbedTests).
/// </summary>
public sealed class McpImageToolTests
{
    private static readonly byte[] Pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6Cj8AAAAASUVORK5CYII=");

    [PostgreSqlFact]
    public async Task Images_and_file_links_are_Markdown_and_their_look_changes_on_the_side_in_the_draft_only()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Příručka VPN", Rich(Heading(2, "uvod", "Úvod"), Paragraph("Publikovaný úvod."),
            Heading(2, "postup", "Postup"), Paragraph("Krok.")), "Publikovaný úvod.");
        var image = await UploadAsync(f.Admin, page.Id, Pixel, "schema.png", "image");
        var file = await UploadAsync(f.Admin, page.Id, "%PDF-1.4 příručka"u8.ToArray(), "prirucka.pdf", "file");
        var (tokenId, client) = await DraftClientAsync(f);

        var introduction = $"## Úvod\n\nPublikovaný úvod.\n\n![Schéma sítě](/api/attachments/{image}/content)\n\nPříručka: [prirucka.pdf](/api/attachments/{file}/download)\n";
        var inserted = await CallAsync(client, "atlas_update_section", new { pageId = page.Id, expectedVersion = page.Version, headingId = "uvod", markdown = introduction });
        Assert.True(inserted.IsError != true, Text(inserted));
        var withImage = Structured(inserted)["version"]!.GetValue<int>();
        var json = JsonNode.Parse(await CurrentAsync<string>(f, page.Id, "ContentJson"))!["document"]!["content"]!.AsArray();
        Assert.Equal(["heading", "paragraph", "image", "paragraph", "heading", "paragraph"], json.Select(x => x!["type"]!.GetValue<string>()));
        Assert.Equal("/api/attachments/" + file + "/download", json[3]!["content"]![1]!["marks"]![0]!["attrs"]!["href"]!.GetValue<string>());

        // The working read shows the image as the Markdown it was written in; readers still see the publication.
        var working = await CallAsync(client, "atlas_get_page", new { pageId = page.Id, version = "working" });
        Assert.Contains($"![Schéma sítě](/api/attachments/{image}/content)", Text(working), StringComparison.Ordinal);
        Assert.Empty(Structured(working)["components"]!.AsArray());
        Assert.DoesNotContain(image, Text(await CallAsync(client, "atlas_get_page", new { pageId = page.Id })), StringComparison.Ordinal);
        Assert.Equal("mcp", await CurrentAsync<string>(f, page.Id, "Channel"));

        // Width and alignment on the side; the section rewritten with the image's Markdown unchanged keeps them.
        var styled = await CallAsync(client, "atlas_update_embed", new { pageId = page.Id, expectedVersion = withImage, attachmentId = image, width = 50, align = "left" });
        Assert.True(styled.IsError != true, Text(styled));
        var rewritten = await CallAsync(client, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = withImage + 1, headingId = "uvod", markdown = introduction.Replace("Publikovaný úvod.", "Nový úvod.", StringComparison.Ordinal)
        });
        Assert.True(rewritten.IsError != true, Text(rewritten));
        var presentation = JsonNode.Parse(await CurrentAsync<string>(f, page.Id, "ContentJson"))!["document"]!["content"]![2]!["attrs"]!["presentation"]!;
        Assert.Equal((50, "left"), (presentation["width"]!.GetValue<int>(), presentation["align"]!.GetValue<string>()));

        // A new picture in place of the old one: a new attachment; the revision that showed the old one still shows it.
        var replacement = await UploadAsync(f.Admin, page.Id, Pixel, "schema-v2.png", "image");
        var replaced = await CallAsync(client, "atlas_update_embed", new { pageId = page.Id, expectedVersion = withImage + 2, attachmentId = image, replaceWithAttachmentId = replacement });
        Assert.True(replaced.IsError != true, Text(replaced));
        var current = await CurrentAsync<string>(f, page.Id, "ContentJson");
        Assert.Contains("/api/attachments/" + replacement + "/content", current, StringComparison.Ordinal);
        Assert.DoesNotContain(image, current, StringComparison.Ordinal);
        Assert.Equal(1L, await f.Site.ScalarAsync("""
            SELECT count(*) FROM "PageRevisionAttachment" a JOIN "PageRevision" r ON r."Id"=a."RevisionId"
            WHERE r."PageId"=@page AND r."Version"=@version AND a."AttachmentId"=@attachment
            """, ("page", Guid.Parse(page.Id)), ("version", withImage), ("attachment", Guid.Parse(image))));
        Assert.Equal(HttpStatusCode.OK, (await f.Admin.GetAsync($"/api/attachments/{image}/content")).StatusCode);
        Assert.Equal(1L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PagePublication\" WHERE \"PageId\"=@page", ("page", Guid.Parse(page.Id))));
        Assert.Equal(["atlas_update_section:ok:" + withImage, "atlas_update_embed:ok:" + (withImage + 1), "atlas_update_section:ok:" + (withImage + 2),
            "atlas_update_embed:ok:" + (withImage + 3)], await AuditAsync(f, tokenId));
    }

    [PostgreSqlFact]
    public async Task Foreign_and_non_image_attachments_are_refused_and_remote_images_stay_links()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Stránka", Rich(Heading(2, "a", "A"), Paragraph("Text.")), "Text.");
        var other = await PageAsync(f.Admin, space, "Jiná", Rich(Paragraph("Jiná.")), "Jiná.");
        var foreign = await UploadAsync(f.Admin, other.Id, Pixel, "cizi.png", "image");
        var file = await UploadAsync(f.Admin, page.Id, "text"u8.ToArray(), "poznamky.txt", "file");
        var own = await UploadAsync(f.Admin, page.Id, Pixel, "vlastni.png", "image");
        var (_, client) = await DraftClientAsync(f);
        Task<ModelContextProtocol.Protocol.CallToolResult> Section(string markdown, int? version = null)
            => CallAsync(client, "atlas_update_section", new { pageId = page.Id, expectedVersion = version ?? page.Version, headingId = "a", markdown = "## A\n\n" + markdown });

        // Another page's attachment is as unknown as one that does not exist; a file is no image.
        Assert.Equal("attachment_not_found", ErrorCode(await Section($"![Cizí](/api/attachments/{foreign}/content)")));
        Assert.Equal("attachment_not_found", ErrorCode(await Section($"[Cizí](/api/attachments/{Guid.NewGuid()}/download)")));
        Assert.Equal("attachment_not_image", ErrorCode(await Section($"![Soubor](/api/attachments/{file}/content)")));
        var stale = await Section($"![Vlastní](/api/attachments/{own}/content)", page.Version + 5);
        Assert.Equal("version_conflict", ErrorCode(stale));
        Assert.Equal(page.Version, Structured(stale)["currentVersion"]!.GetValue<int>());
        Assert.Equal(1L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageRevision\" WHERE \"PageId\"=@page", ("page", Guid.Parse(page.Id))));

        // A remote image is no embed: it stays a link to its address, with a warning.
        var remote = await Section("![Vzdálený](https://example.com/a.png)");
        Assert.True(remote.IsError != true, Text(remote));
        Assert.Contains("Obrázek z adresy mimo přílohy stránky", Text(remote), StringComparison.Ordinal);
        Assert.DoesNotContain("\"image\"", await CurrentAsync<string>(f, page.Id, "ContentJson"), StringComparison.Ordinal);

        // The owner typing in a browser: the look of an image must not discard it.
        var version = Structured(remote)["version"]!.GetValue<int>();
        var embedded = await Section($"![Vlastní](/api/attachments/{own}/content)", version);
        Assert.True(embedded.IsError != true, Text(embedded));
        var document = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/pages/{page.Id}/collaboration"),
            HttpStatusCode.OK))["documentId"]!.GetValue<string>();
        await new PostgresCollaborationStore(f.Site.Services.GetRequiredService<IServiceScopeFactory>())
            .AppendUpdateAsync(document, Guid.NewGuid(), f.AdminId, DateTimeOffset.UtcNow, new byte[] { 1 }, default);
        Assert.Equal("collaboration_unsaved", ErrorCode(await CallAsync(client, "atlas_update_embed",
            new { pageId = page.Id, expectedVersion = version + 1, attachmentId = own, width = 30 })));
    }

    // ---- helpers ----

    /// <summary>Uploads through the web's multipart route as the cookie client; returns the attachment id.</summary>
    internal static async Task<string> UploadAsync(HttpClient client, string pageId, byte[] bytes, string name, string kind)
    {
        var csrf = await AtlasHttpSite.Json(await client.GetAsync("/api/auth/csrf"), HttpStatusCode.OK);
        var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", name }, { new StringContent(kind), "kind" } };
        using var upload = new HttpRequestMessage(HttpMethod.Post, $"/api/pages/{pageId}/attachments") { Content = form };
        upload.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        return (await AtlasHttpSite.Json(await client.SendAsync(upload), HttpStatusCode.Created))["id"]!.GetValue<string>();
    }

    private static async Task<(string TokenId, McpClient Client)> DraftClientAsync(McpSite f)
    {
        await f.AdminTokenAsync("read");
        var (id, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"]);
        return (id, await f.ClientAsync(secret));
    }

    private static async Task<T> CurrentAsync<T>(McpSite f, string pageId, string column)
        => (T)(await f.Site.ScalarAsync($"SELECT \"{column}\" FROM \"PageRevision\" WHERE \"PageId\"=@page AND \"ActiveTo\" IS NULL", ("page", Guid.Parse(pageId))))!;

    private static async Task<string[]> AuditAsync(McpSite f, string tokenId)
    {
        var rows = (string?)await f.Site.ScalarAsync("""
            SELECT string_agg("Operation" || ':' || "Outcome" || ':' || coalesce("ResultVersion"::text,''), '|' ORDER BY "Id") FROM "ApiActivity" WHERE "TokenId"=@token
            """, ("token", Guid.Parse(tokenId)));
        return rows?.Split('|') ?? [];
    }
}
