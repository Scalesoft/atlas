using System.Net;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Ai;

public sealed class AiAccessHttpTests
{
    [PostgreSqlFact]
    public async Task Access_routes_bind_nullable_overrides_and_return_own_consumption_without_cache()
    {
        // Own site: saves the installation's AI access list (GroupAiPolicy, one list with one version) and asserts it starts empty.
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_ai_access_http_");
        using var admin = site.Client();
        var account = await site.CompleteSetupAsync(admin);
        var id = account["id"]!.GetValue<string>();
        var initial = await admin.GetAsync("/api/profile/ai");
        Assert.True(initial.Headers.CacheControl?.NoStore);
        var own = await AtlasHttpSite.Json(initial, HttpStatusCode.OK);
        Assert.False(own["enabled"]!.GetValue<bool>());
        Assert.Equal(4, own["limits"]!.AsArray().Count);
        Assert.False(own["toolsEnabled"]!.GetValue<bool>());
        var row = own["limits"]![0]!;
        Assert.Equal("global", row["origin"]!.GetValue<string>());
        Assert.Equal(0, row["used"]!.GetValue<int>());
        Assert.EndsWith("Z", row["resetAt"]!.GetValue<string>(), StringComparison.Ordinal);

        var endpoint = $"/api/admin/users/{id}/ai-policy";
        var personal = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, endpoint,
            new { enabled = (bool?)null, searchAnswerDailyLimit = (int?)0, chatAnswerDailyLimit = (int?)2,
                pageSummaryDailyLimit = (int?)null, expectedVersion = (string?)null }), HttpStatusCode.OK);
        Assert.Null(personal["enabled"]);
        Assert.Equal(0, personal["searchAnswerDailyLimit"]!.GetValue<int>());
        Assert.False(personal["resolved"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(personal.ToJsonString(), (await AtlasHttpSite.Json(await admin.GetAsync(endpoint), HttpStatusCode.OK)).ToJsonString());

        var access = await AtlasHttpSite.Json(await admin.GetAsync("/api/admin/ai/access"), HttpStatusCode.OK);
        Assert.Empty(access["groups"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/ai/access",
            new { groups = new object?[] { null }, expectedVersion = (string?)null })).StatusCode);

        // D20: a grant from a client that does not know the tools right yet has tools.
        var group = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/groups", new { name = "AI" }),
            HttpStatusCode.OK))["id"]!.GetValue<string>();
        access = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/ai/access",
            new { groups = new[] { new { groupId = group } }, expectedVersion = access["version"]?.GetValue<string>() }), HttpStatusCode.OK);
        Assert.True(access["groups"]![0]!["toolsEnabled"]!.GetValue<bool>());
    }
}