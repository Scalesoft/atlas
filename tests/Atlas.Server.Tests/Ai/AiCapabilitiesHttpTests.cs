using System.Net;
using Atlas.Server.Tests.ExternalSharing;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Ai;

public sealed class AiCapabilitiesHttpTests
{
    [PostgreSqlFact]
    public async Task Capabilities_answer_only_booleans_and_not_to_share_sessions()
    {
        await using var sharing = await SharingSite.CreateAsync();
        var response = await sharing.Admin.GetAsync("/api/ai/capabilities");
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = (await AtlasHttpSite.Json(response, HttpStatusCode.OK)).AsObject();
        // Booleans only: no provider, model, state or reason ever reaches a client.
        Assert.Equal(["semanticSearch", "related", "editorLinks", "createSuggestions", "answer", "assistant", "editorIntent", "editorTransform", "editorOutline", "publishCheck", "changeSummary", "pageSummary", "tagSuggestions", "agent", "arrangePages"], body.Select(x => x.Key));
        Assert.All(body, x => Assert.False(x.Value!.GetValue<bool>()));

        // A visitor of a shared page has no session, so nothing about AI either.
        await sharing.SetPolicy(true, null);
        var link = await AtlasHttpSite.Json(await sharing.Issue(), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.OK, (await sharing.Anonymous.GetAsync("/api/shared/" + link["url"]!.GetValue<string>()[3..])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await sharing.Anonymous.GetAsync("/api/ai/capabilities")).StatusCode);
        // Nor does a personal API token: the endpoint is not part of that API.
        using var bearer = sharing.Site.Client();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", "atl_not-a-session");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/ai/capabilities")).StatusCode);
    }
}
