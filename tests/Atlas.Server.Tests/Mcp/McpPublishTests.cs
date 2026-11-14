using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Mcp;

/// <summary>
/// atlas_publish in 3.6 (owner's decision): the scope write publishes at once, as the token owner may in the web, with the channel mcp,
/// the token and the owner as publisher; a read token neither sees nor runs it. The owner's content rights, unsaved live edits and
/// G5 (spaces excluded from AI) still refuse, and every attempt is audited.
/// </summary>
public sealed class McpPublishTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Publish_needs_write_and_publishes_at_once()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await EditedPageAsync(f, space, "Postup VPN");
        await f.AdminTokenAsync("read");

        // A read token never sees the tool; calling it anyway is scope_missing, audited.
        var (readTokenId, readSecret) = await IssueAsync(f.Admin, f.Site.Password, ["read"], ["mcp"]);
        var reader = await f.ClientAsync(readSecret);
        Assert.DoesNotContain(await reader.ListToolsAsync(), tool => tool.Name == "atlas_publish");
        Assert.Equal("scope_missing", ErrorCode(await CallAsync(reader, "atlas_publish", new { pageId = page.Id, expectedVersion = page.Version })));
        Assert.Equal(["atlas_publish:scope_missing:"], await AuditAsync(f, readTokenId));

        // A write token: the tool is listed as destructive and publishes without any confirmation in the web.
        var (tokenId, publisher) = await WriteClientAsync(f);
        var tool = Assert.Single(await publisher.ListToolsAsync(), x => x.Name == "atlas_publish");
        Assert.True(tool.ProtocolTool.Annotations?.DestructiveHint);
        Assert.False(tool.ProtocolTool.Annotations?.IdempotentHint);
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(publisher, "atlas_publish", new
        {
            pageId = page.Id, expectedVersion = page.Version, changeSummary = new string('x', 501)
        })));
        var published = await CallAsync(publisher, "atlas_publish", new { pageId = page.Id, expectedVersion = page.Version, changeSummary = "Oprava kroků" });
        Assert.True(published.IsError != true, Text(published));
        Assert.Equal("ok", Structured(published)["outcome"]!.GetValue<string>());
        Assert.Equal(page.Version, Structured(published)["publishedVersion"]!.GetValue<int>());
        Assert.Equal(2L, await PublicationsAsync(f, page.Id));
        Assert.Equal("mcp", await PublicationAsync<string>(f, page.Id, "Channel"));
        Assert.Equal(Guid.Parse(tokenId), await PublicationAsync<Guid>(f, page.Id, "ApiTokenId"));
        Assert.Equal(Guid.Parse(f.AdminId), await PublicationAsync<Guid>(f, page.Id, "PublisherId"));
        Assert.Equal("Oprava kroků", await PublicationAsync<string>(f, page.Id, "ChangeSummary"));
        Assert.Contains("atlas_publish:ok:" + page.Version, await AuditAsync(f, tokenId));
        var history = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/pages/{page.Id}/history"), HttpStatusCode.OK);
        Assert.Equal("mcp", history["items"]!.AsArray().Single(x => x!["isCurrent"]!.GetValue<bool>())!["publicationChannel"]!.GetValue<string>());

        // The same version again changes nothing.
        Assert.Equal("already_published", Structured(await CallAsync(publisher, "atlas_publish", new { pageId = page.Id, expectedVersion = page.Version }))["outcome"]!.GetValue<string>());
        Assert.Equal(2L, await PublicationsAsync(f, page.Id));
    }

    [PostgreSqlFact]
    public async Task Lowered_ceiling_refuses_on_the_connected_client()
    {
        await using var f = await McpSite.CreateAsync();
        var page = await EditedPageAsync(f, await f.FirstSpaceAsync(), "Postup VPN");
        var (_, publisher) = await WriteClientAsync(f);

        // The ceiling lowered to read after this client connected: the very next call is refused.
        await f.SetLevelAsync(f.AdminId, "read");
        Assert.DoesNotContain(await publisher.ListToolsAsync(), tool => tool.Name == "atlas_publish");
        Assert.Equal("scope_missing", ErrorCode(await CallAsync(publisher, "atlas_publish", new { pageId = page.Id, expectedVersion = page.Version })));
        Assert.Equal(1L, await PublicationsAsync(f, page.Id));
    }

    /// <summary>A write token never exceeds its owner's rights and never reaches a space excluded from AI, the former publish and
    /// direct tools (publication, its setting) included.</summary>
    [PostgreSqlFact]
    public async Task Write_token_stays_within_owner_rights_and_outside_excluded_spaces()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await EditedPageAsync(f, space, "Postup VPN");
        var (reader, readerId) = await f.UserAsync("ctenar", "write");
        await f.GrantAsync(space, readerId, "Reader");
        var theirs = await f.ClientAsync((await IssueAsync(reader, f.Site.Password, ["read", "write"], ["mcp"])).Secret);
        Assert.True((await CallAsync(theirs, "atlas_publish", new { pageId = page.Id, expectedVersion = page.Version })).IsError);
        // A page the owner may read but not edit is 403 as in REST, not the 404 of a missing page.
        Assert.Equal("forbidden", ErrorCode(await CallAsync(theirs, "atlas_set_publication", new { pageId = page.Id, mode = "Live" })));
        Assert.Equal(1L, await PublicationsAsync(f, page.Id));
        Assert.Equal(DBNull.Value, await f.Site.ScalarAsync("SELECT \"PublicationMode\" FROM \"Page\" WHERE \"Id\"=@page", ("page", Guid.Parse(page.Id))) ?? DBNull.Value);

        // G5: the administrator's own write token cannot publish a page of a space excluded from AI and MCP; it is a missing page.
        var (_, publisher) = await WriteClientAsync(f);
        var excluded = await f.SpaceAsync("Smlouvy");
        var hidden = await PageAsync(f.Admin, excluded, "Smlouva", Rich(Paragraph("Text.")), "Text.", publish: false);
        await f.ExcludeFromAiAsync(excluded);
        Assert.Equal("not_found", ErrorCode(await CallAsync(publisher, "atlas_publish", new { pageId = hidden.Id, expectedVersion = hidden.Version })));
        Assert.Equal("not_found", ErrorCode(await CallAsync(publisher, "atlas_set_publication", new { pageId = hidden.Id, mode = "Live" })));
        Assert.Equal(0L, await PublicationsAsync(f, hidden.Id));
    }

    [PostgreSqlFact]
    public async Task Unsaved_live_edits_block_publication()
    {
        await using var f = await McpSite.CreateAsync();
        var page = await EditedPageAsync(f, await f.FirstSpaceAsync(), "Postup VPN");
        var (_, publisher) = await WriteClientAsync(f);
        var arguments = new { pageId = page.Id, expectedVersion = page.Version };

        // The owner typing in a browser refuses the publication; once saved, the same call publishes.
        var document = await OpenAsync(f.Admin, page.Id);
        await AppendAsync(f, document, f.AdminId);
        Assert.Equal("collaboration_unsaved", ErrorCode(await CallAsync(publisher, "atlas_publish", arguments)));
        Assert.Equal(1L, await PublicationsAsync(f, page.Id));
        await MarkSavedAsync(f, page.Id);
        var published = await CallAsync(publisher, "atlas_publish", arguments);
        Assert.True(published.IsError != true, Text(published));
        Assert.Equal(2L, await PublicationsAsync(f, page.Id));
    }

    // ---- helpers ----

    /// <summary>A published page with a newer working revision (one section changed, a link to a page that does not exist).</summary>
    private static async Task<(string Id, int Version)> EditedPageAsync(McpSite f, string space, string title)
    {
        var page = await PageAsync(f.Admin, space, title, Rich(Heading(2, "a", "A"), Paragraph("Publikováno."), Heading(2, "b", "B"), Paragraph("Beze změny.")),
            "Publikováno.");
        var link = """{"type":"paragraph","content":[{"type":"pageReference","attrs":{"id":""" + JsonSerializer.Serialize(Guid.NewGuid().ToString()) +
            ""","title":"Zmizelá","url":"#/pages/zmizela"}}]}""";
        var version = await SaveAsync(f.Admin, page.Id, page.Version, title,
            Rich(Heading(2, "a", "A"), Paragraph("Upraveno."), link, Heading(2, "b", "B"), Paragraph("Beze změny.")), "Upraveno.");
        return (page.Id, version);
    }

    /// <summary>The administrator's token with read and write on the channel mcp: its id and a connected client.</summary>
    private static async Task<(string TokenId, McpClient Client)> WriteClientAsync(McpSite f)
    {
        await f.AdminTokenAsync("read");
        var (id, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"]);
        return (id, await f.ClientAsync(secret));
    }

    private static async Task<long> PublicationsAsync(McpSite f, string pageId)
        => (long)(await f.Site.ScalarAsync("SELECT count(*) FROM \"PagePublication\" WHERE \"PageId\"=@page", ("page", Guid.Parse(pageId))))!;

    private static async Task<T> PublicationAsync<T>(McpSite f, string pageId, string column)
        => (T)(await f.Site.ScalarAsync($"SELECT \"{column}\" FROM \"PagePublication\" WHERE \"PageId\"=@page AND \"ActiveTo\" IS NULL", ("page", Guid.Parse(pageId))))!;

    /// <summary>The audit rows of a token in order: tool:outcome:version.</summary>
    private static async Task<string[]> AuditAsync(McpSite f, string tokenId)
    {
        var rows = (string?)await f.Site.ScalarAsync("""
            SELECT string_agg("Operation" || ':' || "Outcome" || ':' || coalesce("ResultVersion"::text,''), '|' ORDER BY "Id") FROM "ApiActivity" WHERE "TokenId"=@token
            """, ("token", Guid.Parse(tokenId)));
        return rows?.Split('|') ?? [];
    }

    private static async Task<string> OpenAsync(HttpClient browser, string pageId)
        => (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(browser, HttpMethod.Post, $"/api/pages/{pageId}/collaboration"), HttpStatusCode.OK))["documentId"]!.GetValue<string>();

    private static async Task AppendAsync(McpSite f, string documentId, string userId)
    {
        var store = new PostgresCollaborationStore(f.Site.Services.GetRequiredService<IServiceScopeFactory>());
        await store.AppendUpdateAsync(documentId, Guid.NewGuid(), userId, DateTimeOffset.UtcNow, new byte[] { 1 }, default);
    }

    /// <summary>As if the hub had saved the live edits: the checkpoint reaches the last update.</summary>
    private static async Task MarkSavedAsync(McpSite f, string pageId)
        => await f.Site.ScalarAsync("UPDATE \"PageCollaboration\" SET \"LastSavedSequence\"=\"Sequence\" WHERE \"PageId\"=@page", ("page", Guid.Parse(pageId)));
}
