using System.Net;
using System.Security.Cryptography;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Atlas.Server.Features.Mcp.Tools;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Mcp;

/// <summary>
/// The attachment tools through the C# SDK client: an upload stores a private attachment and changes nothing on the page, a download
/// returns the original bytes in stateless parts, refusals leave neither a blob nor metadata, and G5 hides attachments of an excluded
/// space as if they did not exist. The visibility rules of the list and the metadata are the shared catalog's (AttachmentHandlerTests).
/// </summary>
public sealed class McpAttachmentToolTests
{
    [PostgreSqlFact]
    public async Task Upload_keeps_the_page_unchanged_and_download_returns_the_original_in_parts()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Síťové schéma", Rich(Paragraph("Text.")), "Text.");
        var (tokenId, client) = await DraftClientAsync(f);
        var before = await PageStateAsync(f, page.Id);
        var png = Png(70_000);
        var encoded = Convert.ToBase64String(png);

        var uploaded = await CallAsync(client, "atlas_upload_attachment", new { pageId = page.Id, kind = "image", fileName = "C:\\export\\schema.png", contentBase64 = encoded, sha256 = Convert.ToHexStringLower(SHA256.HashData(png)) });
        Assert.True(uploaded.IsError != true, Text(uploaded));
        var result = Structured(uploaded);
        var id = result["attachmentId"]!.GetValue<string>();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(png));
        Assert.Equal("schema.png", result["name"]!.GetValue<string>());
        Assert.Equal("image/png", result["mimeType"]!.GetValue<string>());
        Assert.Equal(png.Length, result["size"]!.GetValue<long>());
        Assert.Equal(sha256, result["sha256"]!.GetValue<string>());
        Assert.Equal($"/api/attachments/{id}/content", result["url"]!.GetValue<string>());
        // The page is untouched: no revision, no version, no publication. The attachment belongs to the token owner.
        Assert.Equal(before, await PageStateAsync(f, page.Id));
        Assert.Equal(Guid.Parse(f.AdminId), await f.Site.ScalarAsync("SELECT \"UploadedBy\" FROM \"Attachment\" WHERE \"Id\"=@id", ("id", Guid.Parse(id))));
        // A browser session downloads the same bytes; the attempt is audited without the payload, and the log never sees it.
        Assert.Equal(png, await f.Admin.GetByteArrayAsync(result["downloadUrl"]!.GetValue<string>()));
        Assert.Equal(["atlas_upload_attachment:ok"], await AuditAsync(f, tokenId));
        Assert.DoesNotContain(encoded[..64], string.Join("\n", f.Site.Logs.Messages), StringComparison.Ordinal);

        // A stateless ranged download: every part names the whole file, the parts join into the original.
        var joined = new MemoryStream();
        long? offset = 0;
        var parts = 0;
        while (offset is { } from)
        {
            var chunk = await CallAsync(client, "atlas_download_attachment", new { attachmentId = id, offset = from, length = 32_768 });
            Assert.True(chunk.IsError != true, Text(chunk));
            var blob = Assert.IsType<BlobResourceContents>(Assert.Single(chunk.Content.OfType<EmbeddedResourceBlock>()).Resource);
            Assert.Equal("image/png", blob.MimeType);
            joined.Write(blob.DecodedData.Span);
            var facts = Structured(chunk);
            Assert.Equal(sha256, facts["sha256"]!.GetValue<string>());
            Assert.Equal(png.Length, facts["size"]!.GetValue<long>());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(blob.DecodedData.Span)), facts["chunkSha256"]!.GetValue<string>());
            offset = facts["complete"]!.GetValue<bool>() ? null : facts["nextOffset"]!.GetValue<long>();
            parts++;
        }
        Assert.Equal(3, parts);
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(joined.ToArray())));
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "atlas_download_attachment", new { attachmentId = id, offset = png.Length })));
        // Uploaded, not used: neither revision references it yet.
        var item = Assert.Single(Items(await CallAsync(client, "atlas_list_attachments", new { pageId = page.Id })))!;
        Assert.Equal(id, item["attachmentId"]!.GetValue<string>());
        Assert.False(item["usedInPublished"]!.GetValue<bool>());
        Assert.False(item["usedInWorking"]!.GetValue<bool>());
    }

    [PostgreSqlFact]
    public async Task Refused_uploads_leave_neither_blob_nor_metadata()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Přílohy", Rich(Paragraph("Text.")), "Text.");
        var excludedSpace = await f.SpaceAsync("Vyloučený");
        var excluded = await PageAsync(f.Admin, excludedSpace, "Mimo MCP", Rich(Paragraph("Text.")), "Text.");
        await f.ExcludeFromAiAsync(excludedSpace);
        var (readerClient, readerId) = await f.UserAsync("reader", "write");
        await f.GrantAsync(space, readerId, "Reader");
        var (tokenId, client) = await DraftClientAsync(f);
        var pngBytes = Png(100);
        var png = Convert.ToBase64String(pngBytes);
        var pngHash = Convert.ToHexString(SHA256.HashData(pngBytes));

        // A page only readable is the 403 REST gives; one of an excluded space and an unknown one are the same 404.
        var spaceReader = await f.ClientAsync((await IssueAsync(readerClient, f.Site.Password, ["read", "write"], ["mcp"])).Secret);
        Assert.Equal("forbidden", ErrorCode(await CallAsync(spaceReader, "atlas_upload_attachment",
            new { pageId = page.Id, kind = "image", fileName = "a.png", contentBase64 = png, sha256 = pngHash })));
        var missing = new List<CallToolResult>
        {
            await CallAsync(client, "atlas_upload_attachment", new { pageId = excluded.Id, kind = "image", fileName = "a.png", contentBase64 = png, sha256 = pngHash }),
            await CallAsync(client, "atlas_upload_attachment", new { pageId = Guid.NewGuid().ToString(), kind = "file", fileName = "a.txt", contentBase64 = png, sha256 = pngHash })
        };
        Assert.All(missing, result => Assert.Equal("not_found", ErrorCode(result)));
        Assert.Single(missing.Select(result => Structured(result).ToJsonString()).Distinct());

        // Malformed base64, SVG named .png as an image, and an argument over the MCP limit.
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "atlas_upload_attachment",
            new { pageId = page.Id, kind = "file", fileName = "a.txt", contentBase64 = "SGVs bG8", sha256 = pngHash })));
        var svg = await CallAsync(client, "atlas_upload_attachment", new
        {
            pageId = page.Id, kind = "image", fileName = "looks-like.png", contentBase64 = Convert.ToBase64String("<svg onload='alert(1)'/>"u8.ToArray()),
            sha256 = Convert.ToHexString(SHA256.HashData("<svg onload='alert(1)'/>"u8))
        });
        Assert.Equal(400, Structured(svg)["status"]!.GetValue<int>());
        Assert.Contains("PNG, JPEG, GIF nebo WebP", Text(svg));
        var tooLarge = await CallAsync(client, "atlas_upload_attachment", new
        {
            pageId = page.Id, kind = "file", fileName = "big.bin", contentBase64 = new string('A', McpAttachmentTools.MaximumUploadBase64Length + 4), sha256 = pngHash
        });
        Assert.Equal("attachment_too_large", ErrorCode(tooLarge));
        Assert.Equal(413, Structured(tooLarge)["status"]!.GetValue<int>());
        Assert.Contains("POST /api/v1/pages/{pageId}/attachments", Text(tooLarge));

        // The blob folder is shared by every test (another test's PNG may be in it), so only this administrator's metadata is counted.
        Assert.Equal(0L, await f.Site.ScalarAsync("SELECT count(*) FROM \"Attachment\" WHERE \"UploadedBy\"=@admin", ("admin", Guid.Parse(f.AdminId))));
        Assert.Equal(["atlas_upload_attachment:not_found", "atlas_upload_attachment:not_found", "atlas_upload_attachment:invalid_argument",
            "atlas_upload_attachment:invalid_argument", "atlas_upload_attachment:attachment_too_large"], await AuditAsync(f, tokenId));
    }

    [PostgreSqlFact]
    public async Task Upload_with_a_wrong_or_malformed_sha256_stores_nothing()
    {
        await using var f = await McpSite.CreateAsync();
        var page = await PageAsync(f.Admin, await f.FirstSpaceAsync(), "Schéma", Rich(Paragraph("Text.")), "Text.");
        var (tokenId, client) = await DraftClientAsync(f);
        var png = Png(100);
        var corrupt = (byte[])png.Clone();
        corrupt[^3] ^= 0xFF; // still a valid PNG signature, the very damage the hash exists to catch

        var mismatch = await CallAsync(client, "atlas_upload_attachment", new
        {
            pageId = page.Id, kind = "image", fileName = "a.png", contentBase64 = Convert.ToBase64String(corrupt), sha256 = Convert.ToHexString(SHA256.HashData(png))
        });
        Assert.Equal("hash_mismatch", ErrorCode(mismatch));
        Assert.Equal(400, Structured(mismatch)["status"]!.GetValue<int>());
        var malformed = await CallAsync(client, "atlas_upload_attachment", new
        {
            pageId = page.Id, kind = "image", fileName = "a.png", contentBase64 = Convert.ToBase64String(png), sha256 = "xyz"
        });
        Assert.Equal("invalid_argument", ErrorCode(malformed));

        Assert.Equal(0L, await f.Site.ScalarAsync("SELECT count(*) FROM \"Attachment\" WHERE \"UploadedBy\"=@admin", ("admin", Guid.Parse(f.AdminId))));
        Assert.Equal(["atlas_upload_attachment:hash_mismatch", "atlas_upload_attachment:invalid_argument"], await AuditAsync(f, tokenId));
    }

    [PostgreSqlFact]
    public async Task Attachments_of_an_excluded_space_are_missing_even_by_id()
    {
        await using var f = await McpSite.CreateAsync();
        var hiddenSpace = await f.SpaceAsync("Vedení");
        var page = await PageAsync(f.Admin, hiddenSpace, "Rozpočet", Rich(Paragraph("Text.")), "Text.");
        var (_, client) = await DraftClientAsync(f);
        var uploaded = await CallAsync(client, "atlas_upload_attachment", new
        {
            pageId = page.Id, kind = "file", fileName = "rozpocet.txt", contentBase64 = Convert.ToBase64String("tajné"u8.ToArray()), sha256 = Convert.ToHexStringLower(SHA256.HashData("tajné"u8))
        });
        var id = Structured(uploaded)["attachmentId"]!.GetValue<string>();
        Assert.Equal("rozpocet.txt", Structured(await CallAsync(client, "atlas_get_attachment", new { attachmentId = id }))["name"]!.GetValue<string>());

        await f.ExcludeFromAiAsync(hiddenSpace);
        // The id is known, the account may still read the page in the web: MCP answers as for an id that never existed.
        var unknown = await CallAsync(client, "atlas_get_attachment", new { attachmentId = Guid.NewGuid().ToString() });
        Assert.Equal("not_found", ErrorCode(unknown));
        foreach (var result in new[]
        {
            await CallAsync(client, "atlas_get_attachment", new { attachmentId = id }),
            await CallAsync(client, "atlas_download_attachment", new { attachmentId = id })
        })
        {
            Assert.Equal(Structured(unknown).ToJsonString(), Structured(result).ToJsonString());
            Assert.Empty(result.Content.OfType<EmbeddedResourceBlock>());
        }
        Assert.Equal("not_found", ErrorCode(await CallAsync(client, "atlas_list_attachments", new { pageId = page.Id })));
        Assert.Equal(HttpStatusCode.OK, (await f.Admin.GetAsync($"/api/attachments/{id}/download")).StatusCode);
    }

    // ---- helpers ----

    /// <summary>A PNG signature and IHDR chunk header followed by random bytes: what the shared signature check accepts.</summary>
    internal static byte[] Png(int size)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>The administrator's token with read and draft on the channel mcp: its id and a connected client.</summary>
    internal static async Task<(string TokenId, McpClient Client)> DraftClientAsync(McpSite f)
    {
        await f.AdminTokenAsync("read");
        var (id, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"]);
        return (id, await f.ClientAsync(secret));
    }

    /// <summary>Version, revision count, published revision and publication count of a page.</summary>
    private static async Task<string> PageStateAsync(McpSite f, string pageId)
        => (string)(await f.Site.ScalarAsync("""
            SELECT p."Version"::text || ':' || (SELECT count(*) FROM "PageRevision" r WHERE r."PageId"=p."Id")::text || ':' ||
                coalesce(p."PublishedRevisionId"::text, '') || ':' || (SELECT count(*) FROM "PagePublication" x WHERE x."PageId"=p."Id")::text
            FROM "Page" p WHERE p."Id"=@page
            """, ("page", Guid.Parse(pageId))))!;

    /// <summary>The audit rows of a token in order: tool:outcome.</summary>
    private static async Task<string[]> AuditAsync(McpSite f, string tokenId)
    {
        var rows = (string?)await f.Site.ScalarAsync("""
            SELECT string_agg("Operation" || ':' || "Outcome", '|' ORDER BY "Id") FROM "ApiActivity" WHERE "TokenId"=@token
            """, ("token", Guid.Parse(tokenId)));
        return rows?.Split('|') ?? [];
    }
}
