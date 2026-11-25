using System.Net;
using System.Net.Http.Headers;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.PersonalLibrary;

/// <summary>What HTTP and MCP add to Co se změnilo (its rules are RecentChangesTests'): the web route, /api/v1 and the read tool, AI parity.</summary>
public sealed class RecentChangesHttpTests
{
    [PostgreSqlFact]
    public async Task The_web_the_personal_api_and_mcp_list_the_same_change()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.SpaceAsync("Změny");
        // An author of the one space: a wiki administrator's "all" is every test's change on the shared site.
        var (author, authorId) = await f.UserAsync("autor");
        await f.GrantAsync(space, authorId, "Writer");
        var (page, _) = await PageAsync(author, space, "Plán", Rich(Paragraph("text")), "text");

        var web = (await AtlasHttpSite.Json(await author.GetAsync("/api/me/changes?filter=all&limit=5"), HttpStatusCode.OK))["items"]![0]!;
        Assert.Equal((page, "Plán", "web", false), (web["pageId"]!.GetValue<string>(), web["title"]!.GetValue<string>(), web["channel"]!.GetValue<string>(),
            web["isAi"]!.GetValue<bool>()));
        Assert.Equal(HttpStatusCode.BadRequest, (await author.GetAsync("/api/me/changes?filter=other")).StatusCode);

        using var api = f.Site.Client();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await IssueAsync(author, f.Site.Password, ["read"], ["rest"], "Reader")).Secret);
        Assert.Equal(page, (await AtlasHttpSite.Json(await api.GetAsync("/api/v1/me/changes"), HttpStatusCode.OK))["items"]![0]!["pageId"]!.GetValue<string>());
        // A person's activity: the author's changes, nobody else's.
        Assert.Equal(page, (await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/me/changes?author={authorId}&space={space}"), HttpStatusCode.OK))["items"]![0]!["pageId"]!.GetValue<string>());
        Assert.Empty((await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/me/changes?author={Guid.NewGuid()}"), HttpStatusCode.OK))["items"]!.AsArray());

        var agent = await f.ClientAsync((await IssueAsync(author, f.Site.Password, ["read"], ["mcp"])).Secret);
        var listed = Assert.Single(Items(await CallAsync(agent, "atlas_list_recent_changes")))!;
        Assert.Equal((page, "Plán"), (listed["pageId"]!.GetValue<string>(), listed["title"]!.GetValue<string>()));
        Assert.Single(Items(await CallAsync(agent, "atlas_list_recent_changes", new { authorId = authorId.ToString() })));
        await f.ExcludeFromAiAsync(space);
        Assert.Empty(Items(await CallAsync(agent, "atlas_list_recent_changes")));
        Assert.Empty(Items(await CallAsync(agent, "atlas_list_recent_changes", new { authorId = authorId.ToString() })));
        // The personal API's tokens serve AI tools too: a space out of AI is gone there as well.
        Assert.Empty((await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/me/changes?author={authorId}"), HttpStatusCode.OK))["items"]!.AsArray());
    }
}
