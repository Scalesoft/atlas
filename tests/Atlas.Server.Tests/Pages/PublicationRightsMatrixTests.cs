using System.Net;
using System.Net.Http.Headers;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Pages;

/// <summary>
/// Rights matrix of the 3.6 publication and history paths: „Publikace“ over HTTP and MCP, „Uložit verzi“ and the history's private 304.
/// The live writes (undo over HTTP and /api/v1, every MCP live tool) are in McpLiveWriteTests.
/// </summary>
public sealed class PublicationRightsMatrixTests
{
    [PostgreSqlFact]
    public async Task Publication_and_named_versions_follow_the_page_rights_and_mcp_needs_write()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Postup", Rich(Paragraph("Text.")), "Text.");
        var (reader, readerId) = await f.UserAsync("reader");
        await f.GrantAsync(space, readerId, "Reader");
        var (stranger, _) = await f.UserAsync("stranger");
        var publication = $"/api/pages/{page.Id}/publication";

        // A reader sees the publication and may not change it; without access the page does not exist.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(publication)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(reader, HttpMethod.Put, publication, new { mode = "Live" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(publication)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(stranger, HttpMethod.Put, publication, new { mode = "Live" })).StatusCode);

        // „Uložit verzi“ is a writer's.
        var named = await f.Site.ScalarAsync("""SELECT count(*) FROM "PageRevision" WHERE "PageId"=@page AND "IsExplicit" """, ("page", Guid.Parse(page.Id)));
        Assert.Contains((await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, $"/api/pages/{page.Id}/versions", new { expectedVersion = page.Version, label = "Moje" }))
            .StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Equal(named, await f.Site.ScalarAsync("""SELECT count(*) FROM "PageRevision" WHERE "PageId"=@page AND "IsExplicit" """, ("page", Guid.Parse(page.Id))));

        // atlas_set_publication publishes without a person's confirmation: it needs the write scope (3.6 tokens are read or write).
        await f.SetLevelAsync(f.AdminId, "write");
        var reading = await f.ClientAsync((await IssueAsync(f.Admin, f.Site.Password, ["read"], ["mcp"], "Read")).Secret);
        Assert.Equal("scope_missing", ErrorCode(await CallAsync(reading, "atlas_set_publication", new { pageId = page.Id, mode = "Live" })));
        Assert.Equal("Manual", (await AtlasHttpSite.Json(await f.Admin.GetAsync(publication), HttpStatusCode.OK))["effective"]!.GetValue<string>());
        var writing = await f.ClientAsync((await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"], "Write")).Secret);
        Assert.True((await CallAsync(writing, "atlas_set_publication", new { pageId = page.Id, mode = "Live" })).IsError != true);
        Assert.Equal("Live", (await AtlasHttpSite.Json(await f.Admin.GetAsync(publication), HttpStatusCode.OK))["effective"]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task A_kept_history_is_not_revalidated_once_access_is_revoked()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Postup", Rich(Paragraph("Text.")), "Text.");
        var (writer, writerId) = await f.UserAsync("writer");
        await f.GrantAsync(space, writerId, "Writer");
        var history = $"/api/pages/{page.Id}/history";
        var first = await writer.GetAsync(history);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var tag = first.Headers.ETag!;
        using (var again = new HttpRequestMessage(HttpMethod.Get, history))
        {
            again.Headers.IfNoneMatch.Add(tag);
            Assert.Equal(HttpStatusCode.NotModified, (await writer.SendAsync(again)).StatusCode);
        }

        await f.Site.ExecuteAsync("""UPDATE "SpaceGrant" SET "ActiveTo"=clock_timestamp() WHERE "UserId"=@user AND "ActiveTo" IS NULL""",
            ("user", Guid.Parse(writerId)));

        using var revoked = new HttpRequestMessage(HttpMethod.Get, history);
        revoked.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(tag.Tag));
        Assert.Equal(HttpStatusCode.NotFound, (await writer.SendAsync(revoked)).StatusCode);
    }
}
