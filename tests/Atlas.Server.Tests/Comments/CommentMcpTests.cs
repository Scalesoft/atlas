using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.Projector;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Comments;

/// <summary>AI parity of discussions (5.2): an agent reads, answers, edits and deletes an object's comments over MCP and /api/v1 with the
/// token owner's rights and the token's scope; a space out of AI is missing to MCP and REST, not to the web.</summary>
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

        // G5: once the space is out of AI, neither MCP nor REST (whose tokens AI tools use too) finds comments there; the web still does.
        await f.ExcludeFromAiAsync(space);
        Assert.Equal("not_found", ErrorCode(await CallAsync(reading, "atlas_list_comments", new { objectId = page.Id })));
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/v1/objects/{page.Id}/comments")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/objects/{page.Id}/comments")).StatusCode);
    }

    /// <summary>The web's rights over MCP and /api/v1: the author edits and deletes its comment, the object's manager deletes anyone's
    /// but edits only its own, a reader of the space touches no one else's, an account that may not read the object finds nothing; a read
    /// token writes nothing. Every attempt is audited under its tool or operation; a space out of AI is missing to MCP.</summary>
    [PostgreSqlFact]
    public async Task An_agent_edits_and_deletes_comments_with_the_webs_rights()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.SpaceAsync("Diskuse");
        var page = await PageAsync(f.Admin, space, "Postup", Rich(Paragraph("Text.")), "Text.");
        var (reader, readerId) = await f.UserAsync("reader", "write");
        await f.GrantAsync(space, readerId, "Reader");
        var (outsider, _) = await f.UserAsync("outsider", "write");
        var managers = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/objects/{page.Id}/comments",
            new { body = "Od správce." }), HttpStatusCode.Created))["id"]!.GetValue<string>();

        var (writeId, writeSecret) = await IssueAsync(reader, f.Site.Password, ["read", "write"], ["mcp"], "Write");
        var writing = await f.ClientAsync(writeSecret);
        var own = Structured(await CallAsync(writing, "atlas_post_comment", new { objectId = page.Id, body = "První." }))["commentId"]!.GetValue<string>();
        var edited = Structured(await CallAsync(writing, "atlas_edit_comment", new { objectId = page.Id, commentId = own, body = "Opraveno." }));
        Assert.Equal(own, edited["commentId"]!.GetValue<string>());
        Assert.NotNull(edited["editedAt"]);
        // A reader of the space touches no one else's comment.
        Assert.Equal("forbidden", ErrorCode(await CallAsync(writing, "atlas_edit_comment", new { objectId = page.Id, commentId = managers, body = "Ne." })));
        Assert.Equal("forbidden", ErrorCode(await CallAsync(writing, "atlas_delete_comment", new { objectId = page.Id, commentId = managers })));
        // A read token writes nothing; an account that may not read the object finds nothing to change.
        var (readId, readSecret) = await IssueAsync(reader, f.Site.Password, ["read"], ["mcp"], "Read");
        var reading = await f.ClientAsync(readSecret);
        Assert.Equal("scope_missing", ErrorCode(await CallAsync(reading, "atlas_edit_comment", new { objectId = page.Id, commentId = own, body = "Ne." })));
        Assert.Equal("scope_missing", ErrorCode(await CallAsync(reading, "atlas_delete_comment", new { objectId = page.Id, commentId = own })));
        var stranger = await f.ClientAsync((await IssueAsync(outsider, f.Site.Password, ["read", "write"], ["mcp"], "Outsider")).Secret);
        Assert.Equal("not_found", ErrorCode(await CallAsync(stranger, "atlas_edit_comment", new { objectId = page.Id, commentId = own, body = "Ne." })));
        Assert.Equal("not_found", ErrorCode(await CallAsync(stranger, "atlas_delete_comment", new { objectId = page.Id, commentId = own })));
        // The object's manager deletes anyone's comment but edits only its own; a deleted comment stays deleted.
        await f.AdminTokenAsync("read");
        var manager = await f.ClientAsync((await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"], "Manager")).Secret);
        Assert.Equal("forbidden", ErrorCode(await CallAsync(manager, "atlas_edit_comment", new { objectId = page.Id, commentId = own, body = "Ne." })));
        Assert.NotEqual(true, (await CallAsync(manager, "atlas_delete_comment", new { objectId = page.Id, commentId = own })).IsError);
        Assert.Equal("comment_deleted", ErrorCode(await CallAsync(writing, "atlas_delete_comment", new { objectId = page.Id, commentId = own })));
        var web = (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/objects/{page.Id}/comments"), HttpStatusCode.OK))["items"]!.AsArray();
        Assert.Equal((own, true), (web[0]!["id"]!.GetValue<string>(), web[0]!["deleted"]!.GetValue<bool>()));
        Assert.Equal((managers, "Od správce."), (web[1]!["id"]!.GetValue<string>(), web[1]!["body"]!.GetValue<string>()));

        // /api/v1: the same rules with the write scope.
        var (restId, restSecret) = await IssueAsync(reader, f.Site.Password, ["read", "write"], ["rest"], "Rest");
        using var api = f.Site.Client();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", restSecret);
        var second = (await AtlasHttpSite.Json(await api.PostAsJsonAsync($"/api/v1/objects/{page.Id}/comments", new { body = "Přes API." }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        var put = await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/objects/{page.Id}/comments/{second}", new { body = "Přes API, opraveno." }), HttpStatusCode.OK);
        Assert.Equal("Přes API, opraveno.", put["body"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PutAsJsonAsync($"/api/v1/objects/{page.Id}/comments/{managers}", new { body = "Ne." })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.DeleteAsync($"/api/v1/objects/{page.Id}/comments/{managers}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/v1/objects/{page.Id}/comments/{second}")).StatusCode);
        using var readApi = f.Site.Client();
        readApi.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await IssueAsync(reader, f.Site.Password, ["read"], ["rest"], "Rest read")).Secret);
        Assert.Equal(HttpStatusCode.Forbidden, (await readApi.PutAsJsonAsync($"/api/v1/objects/{page.Id}/comments/{managers}", new { body = "Ne." })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readApi.DeleteAsync($"/api/v1/objects/{page.Id}/comments/{managers}")).StatusCode);

        // Every attempt is audited, refusals included.
        Assert.Equal(["atlas_post_comment:ok:", "atlas_edit_comment:ok:", "atlas_edit_comment:forbidden:", "atlas_delete_comment:forbidden:", "atlas_delete_comment:comment_deleted:"],
            await ProjectorMcpToolTests.AuditAsync(f, writeId));
        Assert.Equal(["atlas_edit_comment:scope_missing:", "atlas_delete_comment:scope_missing:"], await ProjectorMcpToolTests.AuditAsync(f, readId));
        Assert.Equal(["comment.post:ok:", "comment.edit:ok:", "comment.edit:forbidden:", "comment.delete:forbidden:", "comment.delete:ok:"],
            await ProjectorMcpToolTests.AuditAsync(f, restId));

        // G5: once the space is out of AI, MCP finds the comment no more and changes nothing.
        await f.ExcludeFromAiAsync(space);
        Assert.Equal("not_found", ErrorCode(await CallAsync(manager, "atlas_edit_comment", new { objectId = page.Id, commentId = managers, body = "Ne." })));
        Assert.Equal("not_found", ErrorCode(await CallAsync(manager, "atlas_delete_comment", new { objectId = page.Id, commentId = managers })));
        web = (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/objects/{page.Id}/comments"), HttpStatusCode.OK))["items"]!.AsArray();
        Assert.Equal(("Od správce.", false), (web[^1]!["body"]!.GetValue<string>(), web[^1]!["deleted"]!.GetValue<bool>()));
    }

    private static string McpText() => Atlas.Server.Features.Mcp.McpText.TrustNotice;
}
