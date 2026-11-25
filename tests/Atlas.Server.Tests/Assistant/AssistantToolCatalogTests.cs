using System.Net;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Assistant;

/// <summary>The tool list of the assistant panel: the registry the agent turn offers, hidden when the user has no tools.</summary>
public sealed class AssistantToolCatalogTests
{
    [PostgreSqlFact]
    public async Task Lists_the_offered_tools_marks_writes_and_hides_them_without_the_right()
    {
        await using var f = await AnswerHttpTests.AssistantSiteAsync(nameof(AssistantToolCatalogTests));
        var (reader, readerId) = await f.UserAsync("reader", level: null);
        var open = await f.SpaceAsync("Provoz");
        await f.GrantAsync(open, readerId, "Reader");
        await PageAsync(f.Admin, open, "Připojení k VPN", Rich(Paragraph("Postup vpnkoala pro všechny.")), "Postup vpnkoala pro všechny.");
        await AnswerHttpTests.EnableAnswersAsync(f.Site, f.Admin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Site.Client().GetAsync("/api/assistant/tools")).StatusCode);

        await SetToolsAsync(f, readerId, true);
        var groups = await AtlasHttpSite.Json(await reader.GetAsync("/api/assistant/tools"), HttpStatusCode.OK);
        var tools = groups.AsArray().SelectMany(group => group!["tools"]!.AsArray()).ToDictionary(tool => tool!["name"]!.GetValue<string>(), tool => tool!);
        Assert.False(tools["atlas_search"]["write"]!.GetValue<bool>());
        Assert.True(tools["atlas_apply_labels"]["write"]!.GetValue<bool>());
        Assert.All(tools.Values, tool => Assert.NotEmpty(tool["description"]!.GetValue<string>()));
        Assert.DoesNotContain("atlas_trash_page", tools.Keys);
        Assert.DoesNotContain("atlas_download_attachment", tools.Keys);

        await SetToolsAsync(f, readerId, false);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/api/assistant/tools")).StatusCode);
    }

    private static async Task SetToolsAsync(Mcp.McpSite f, string userId, bool tools)
    {
        var current = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/admin/users/{userId}/ai-policy"), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/admin/users/{userId}/ai-policy",
            new { enabled = true, toolsEnabled = tools, searchAnswerDailyLimit = (int?)null, chatAnswerDailyLimit = (int?)null, pageSummaryDailyLimit = (int?)null,
                agentDailyLimit = (int?)null, expectedVersion = current["version"]?.GetValue<string>() }), HttpStatusCode.OK);
    }
}
