using System.Net;
using Atlas.Server.Tests.PageLinks;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.AiSuggestions;

/// <summary>What HTTP adds to E2, E4, P1–P3: routes and binding, the canonical 503 <c>ai_unavailable</c> Problem Details, 204 for
/// "nothing to suggest", and a log that never holds the author's working text, at any level, whatever the outcome.</summary>
public sealed class AiSuggestionsHttpTests
{
    private const string Marker = "kvetinac zelezny sedmdesat";

    [PostgreSqlFact]
    public async Task Ai_endpoints_answer_503_ai_unavailable_problem_details_and_never_log_working_text()
    {
        // A site of its own (settings without shared: true): the 503 answers need an installation without AI (AiSetting,
        // AiProfileAssignment), which a shared site cannot promise, and the log assertions read every line of the site.
        await using var site = await AtlasHttpSite.CreateAsync("atlas_ai_suggestions_http_", settings =>
        {
            // Everything the server and the framework would write, at every level.
            settings["Logging:LogLevel:Default"] = "Trace";
            settings["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace";
        });
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = (await AtlasHttpSite.Json(await admin.GetAsync("/api/spaces"), HttpStatusCode.OK))[0]!["id"]!.GetValue<string>();
        var vpn = await PageLinksHttpTests.PublishAsync(admin, spaceId, "VPN", PageLinksFixture.Doc(PageLinksFixture.Text("vpn")));
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Rozepsaná", contentJson = PageLinksFixture.Doc(PageLinksFixture.Text("x")), markdown = "x" }), HttpStatusCode.OK);
        var pageId = page["id"]!.GetValue<string>();
        var title = Uri.EscapeDataString(Marker + " sablona");

        // E2 without AI: the exact match, in the contract's shape.
        var links = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/assist/link-candidates",
            new { pageId, phrases = new[] { "VPN", Marker } }), HttpStatusCode.OK);
        var link = Assert.Single(links["items"]!.AsArray())!.AsObject();
        Assert.Equal(["phrase", "pageId", "spaceId", "spaceName", "title", "matchedBy"], link.Select(x => x.Key));
        Assert.Equal(("VPN", vpn["id"]!.GetValue<string>(), "title"),
            (link["phrase"]!.GetValue<string>(), link["pageId"]!.GetValue<string>(), link["matchedBy"]!.GetValue<string>()));

        // E4 and P2 have no rule-based part: the canonical 503.
        foreach (var response in new[]
                 {
                     await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/assist/page-order",
                         new { pageId, context = Marker, candidates = new[] { vpn["id"]!.GetValue<string>() } }),
                     await admin.GetAsync($"/api/pages/placement-suggestion?title={title}&spaceId={spaceId}"),
                 })
        {
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await AtlasHttpSite.Json(response, HttpStatusCode.ServiceUnavailable);
            Assert.Equal(503, problem["status"]!.GetValue<int>());
            Assert.Equal("ai_unavailable", problem["code"]!.GetValue<string>());
            Assert.Equal("Tahle funkce teď není k dispozici.", problem["title"]!.GetValue<string>());
        }
        // P3 and P1 work without AI: nothing to suggest is 204, similar pages answer as in phase 1.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.GetAsync($"/api/pages/template-suggestion?title={title}&spaceId={spaceId}")).StatusCode);
        Assert.Equal(["items"], (await AtlasHttpSite.Json(await admin.GetAsync($"/api/search/similar-pages?title={title}&spaceId={spaceId}"), HttpStatusCode.OK))
            .AsObject().Select(x => x.Key));

        // Failures carry the text too: a missing page (a failed Wolverine invocation) and malformed requests.
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/assist/link-candidates",
            new { pageId = Guid.NewGuid(), phrases = new[] { Marker } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/assist/link-candidates",
            new { pageId, phrases = new[] { Marker + new string('x', 80) } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/pages/template-suggestion?title={title + new string('x', 200)}&spaceId={spaceId}")).StatusCode);


        // The log was written (positive control) and never holds the working text.
        Assert.Contains(site.Logs.Messages, message => message.Contains("/api/assist/link-candidates", StringComparison.Ordinal));
        Assert.DoesNotContain(site.Logs.Messages, message => message.Contains("kvetinac", StringComparison.OrdinalIgnoreCase));
    }
}
