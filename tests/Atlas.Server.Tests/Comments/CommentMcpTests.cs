using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Comments;

/// <summary>AI parity of discussions (5.2): an agent reads and answers an object's comments over MCP and /api/v1 with the token owner's
/// rights and the token's scope; a space out of AI is missing to MCP, not to REST.</summary>
public sealed class CommentMcpTests
{
    [PostgreSqlFact]
    public async Task An_agent_reads_and_answers_comments_with_the_owners_rights_and_the_tokens_scope()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var elsewhere = await f.SpaceAsync("Jinde");
        var page = await PageAsync(f.Admin, space, "Postup", Rich(Paragraph("Text.")), "Text.");
        var hidden = await PageAsync(f.Admin, elsewhere, "Tajné", Rich(Paragraph("Text.")), "Text.");
        var (reader, readerId) = await f.UserAsync("reader", "write");
        await f.GrantAsync(space, readerId, "Reader");
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/objects/{page.Id}/comments",
            new { body = $"Platí to ještě, [Čtenář](a-contact:?name=%C4%8Cten%C3%A1%C5%99&internalUser.id={readerId})?" }), HttpStatusCode.Created);

        var reading = await f.ClientAsync((await IssueAsync(reader, f.Site.Password, ["read"], ["mcp"], "Read")).Secret);
        var listed = Structured(await CallAsync(reading, "atlas_list_comments", new { objectId = page.Id }));
        Assert.Equal((1, true), (listed["count"]!.GetValue<int>(), listed["canPost"]!.GetValue<bool>()));
        Assert.StartsWith("Platí to ještě", listed["items"]![0]!["body"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(McpText(), listed["text"]!.GetValue<string>(), StringComparison.Ordinal);
        // A read token cannot answer; an object the owner may not read is missing.
        Assert.Equal("scope_missing", ErrorCode(await CallAsync(reading, "atlas_post_comment", new { objectId = page.Id, body = "Ano." })));
        Assert.Equal("not_found", ErrorCode(await CallAsync(reading, "atlas_list_comments", new { objectId = hidden.Id })));

        var writing = await f.ClientAsync((await IssueAsync(reader, f.Site.Password, ["read", "write"], ["mcp"], "Write")).Secret);
        var answer = Structured(await CallAsync(writing, "atlas_post_comment", new { objectId = page.Id, body = "Ano, platí." }));
        Assert.Equal("not_found", ErrorCode(await CallAsync(writing, "atlas_post_comment", new { objectId = hidden.Id, body = "x" })));
        // Written as the token's owner: the web shows it under the reader's name, newest first.
        var web = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/objects/{page.Id}/comments"), HttpStatusCode.OK);
        Assert.Equal((answer["commentId"]!.GetValue<string>(), readerId), (web["items"]![0]!["id"]!.GetValue<string>(), web["items"]![0]!["authorId"]!.GetValue<string>()));

        // /api/v1: the same read, and the post with the write scope only, answered Created at its own path.
        using var api = f.Site.Client();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await IssueAsync(reader, f.Site.Password, ["read", "write"], ["rest"], "Rest")).Secret);
        Assert.Equal(2, (await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/objects/{page.Id}/comments"), HttpStatusCode.OK))["count"]!.GetValue<int>());
        var posted = await api.PostAsJsonAsync($"/api/v1/objects/{page.Id}/comments", new { body = "Přes API." });
        var created = await AtlasHttpSite.Json(posted, HttpStatusCode.Created);
        Assert.Equal($"/api/v1/objects/{page.Id}/comments/{created["id"]!.GetValue<string>()}", posted.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/v1/objects/{hidden.Id}/comments")).StatusCode);
        using var readApi = f.Site.Client();
        readApi.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await IssueAsync(reader, f.Site.Password, ["read"], ["rest"], "Rest read")).Secret);
        Assert.Equal(HttpStatusCode.Forbidden, (await readApi.PostAsJsonAsync($"/api/v1/objects/{page.Id}/comments", new { body = "Ne." })).StatusCode);

        // G5: once the space is out of AI, MCP finds no comments there; REST still does.
        await f.ExcludeFromAiAsync(space);
        Assert.Equal("not_found", ErrorCode(await CallAsync(reading, "atlas_list_comments", new { objectId = page.Id })));
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync($"/api/v1/objects/{page.Id}/comments")).StatusCode);
    }

    private static string McpText() => Atlas.Server.Features.Mcp.McpText.TrustNotice;
}
