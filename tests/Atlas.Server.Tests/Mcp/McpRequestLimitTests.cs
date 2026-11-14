using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Mcp.Tools;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Mcp;

/// <summary>
/// The /mcp request limit on a real Kestrel host (TestServer does not enforce an endpoint's body limit). The largest upload the tool
/// takes, 2 MiB as 2 796 204 base64 characters, passes in the JSON-RPC body a JavaScript client such as Claude Code sends and is
/// stored byte for byte; one byte more is the tool's attachment_too_large. A body over the one limit of every call, 3 000 000 B, is
/// refused before the SDK reads it: readably, pointing to REST multipart, when its length is declared, and by Kestrel's limit when the
/// C# SDK client streams it; nothing is stored.
/// </summary>
public sealed class McpRequestLimitTests
{
    [PostgreSqlFact]
    public async Task Largest_upload_passes_a_real_server_and_a_larger_request_is_refused()
    {
        await using var f = await McpSite.CreateAsync(kestrel: true);
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Velké soubory", Rich(Paragraph("Text.")), "Text.");
        var (_, client) = await McpAttachmentToolTests.DraftClientAsync(f);
        var secret = await f.AdminTokenAsync("read", "write");
        using var http = f.Site.Client();

        var largest = RandomNumberGenerator.GetBytes((int)McpAttachmentTools.MaximumUploadBytes);
        var encoded = Convert.ToBase64String(largest);
        Assert.Equal(McpAttachmentTools.MaximumUploadBase64Length, encoded.Length);
        using (var upload = Post(Encoding.UTF8.GetBytes(UploadCall(page.Id, largest)), secret))
        using (var accepted = await http.SendAsync(upload))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var answer = await accepted.Content.ReadAsStringAsync();
            Assert.Contains("\"attachmentId\"", answer, StringComparison.Ordinal);
            Assert.DoesNotContain("\"isError\":true", answer, StringComparison.Ordinal);
        }
        var id = (Guid)(await f.Site.ScalarAsync("SELECT \"Id\" FROM \"Attachment\""))!;
        Assert.Equal(largest, await f.Admin.GetByteArrayAsync($"/api/attachments/{id}/download"));

        // One byte more has the same base64 length without padding: the decoded size refuses it, with the way to REST. (Sent as
        // JavaScript sends it: the C# SDK client escapes "+" and would exceed the request limit first.)
        using (var oneMore = Post(Encoding.UTF8.GetBytes(UploadCall(page.Id, RandomNumberGenerator.GetBytes((int)McpAttachmentTools.MaximumUploadBytes + 1))), secret))
        using (var refusedUpload = await http.SendAsync(oneMore))
        {
            Assert.Equal(HttpStatusCode.OK, refusedUpload.StatusCode);
            var answer = await refusedUpload.Content.ReadAsStringAsync();
            Assert.Contains("\"isError\":true", answer, StringComparison.Ordinal);
            Assert.Contains("attachment_too_large", answer, StringComparison.Ordinal);
            Assert.Contains("POST /api/v1/pages/{pageId}/attachments", answer, StringComparison.Ordinal);
        }

        // One byte over the limit with a declared length: refused unread, with a reason a person can act on.
        using (var oversized = Post(new byte[McpModule.MaximumRequestBytes + 1], secret))
        using (var refused = await http.SendAsync(oversized))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
            var problem = (await refused.Content.ReadFromJsonAsync<JsonNode>())!;
            Assert.Equal("request_too_large", problem["code"]!.GetValue<string>());
            Assert.Contains("POST /api/v1/pages/{pageId}/attachments", problem["title"]!.GetValue<string>());
        }

        // The SDK client streams its body (no declared length): a 3 MiB file is refused by Kestrel before any tool runs.
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => CallAsync(client, "atlas_upload_attachment",
            new { pageId = page.Id, kind = "file", fileName = "streamed.bin", contentBase64 = Convert.ToBase64String(new byte[3 * 1024 * 1024]) }));
        // A streamed body has no length to refuse upfront: Kestrel answers 413 once the limit is read and closes the connection, as it
        // cannot drain the rest. Whether the client reads that answer first or finds its unfinished upload cut off (a reset) is a race
        // of the two sides; a loaded machine (the full parallel run) loses it often. Both are the same refusal, and nothing is stored.
        Assert.True(error.StatusCode == HttpStatusCode.RequestEntityTooLarge || error is { StatusCode: null, InnerException: IOException
        {
            InnerException: System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionReset or System.Net.Sockets.SocketError.Shutdown
                or System.Net.Sockets.SocketError.ConnectionAborted }
        } }, error.ToString());
        Assert.Equal(1L, await f.Site.ScalarAsync("SELECT count(*) FROM \"Attachment\""));
        Assert.Single(Directory.GetFiles(Path.Combine(f.Site.DataPath, "attachments")));
    }

    private static string UploadCall(string pageId, byte[] content)
        => "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"atlas_upload_attachment\",\"arguments\":{\"pageId\":\"" + pageId +
            "\",\"kind\":\"file\",\"fileName\":\"largest.bin\",\"sha256\":\"" + Convert.ToHexStringLower(SHA256.HashData(content)) + "\",\"contentBase64\":\"" +
            Convert.ToBase64String(content) + "\"}}}";

    /// <summary>A stateless JSON-RPC POST as an MCP client sends it, with a declared Content-Length.</summary>
    private static HttpRequestMessage Post(byte[] body, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return request;
    }
}
