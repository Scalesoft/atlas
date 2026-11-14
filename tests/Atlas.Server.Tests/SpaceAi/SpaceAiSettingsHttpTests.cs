using System.Net;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.SpaceAi;

public sealed class SpaceAiSettingsHttpTests
{
    [PostgreSqlFact]
    public async Task Space_ai_settings_bind_included_and_expected_version()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_space_ai_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var path = $"/api/spaces/{await AtlasHttpSite.OwnSpaceAsync(admin)}/ai-settings";

        var initial = await AtlasHttpSite.Json(await admin.GetAsync(path), HttpStatusCode.OK);
        Assert.True(initial["included"]!.GetValue<bool>());
        Assert.Null(initial["version"]);

        var excluded = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path, new { included = false, expectedVersion = (string?)null }), HttpStatusCode.OK);
        Assert.False(excluded["included"]!.GetValue<bool>());
        var version = excluded["version"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Conflict, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path, new { included = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path, new { expectedVersion = version })).StatusCode);
        var included = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, path, new { included = true, expectedVersion = version }), HttpStatusCode.OK);
        Assert.True(included["included"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/spaces/{Guid.NewGuid()}/ai-settings")).StatusCode);
    }
}
