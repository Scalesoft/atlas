using System.Net;
using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class AtlasPipelineTests
{
    /// <summary>
    /// UseAtlasPipeline marks every /api response no-store before routing, so an endpoint never repeats it and no feature test
    /// needs its own assertion. One request of each kind, through the real host: an answer from an endpoint that never set the
    /// header, a refusal before any endpoint runs, a failure written by the exception handler, the unknown-route answer, one
    /// administration read whose endpoint used to set the header itself and one secrets read that used to carry the NoStore filter.
    /// Tagged Smoke because a change under Platform/ runs only Smoke tests and every /api endpoint relies on this one pipeline line.
    /// </summary>
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Every_api_response_is_no_store_whatever_the_endpoint_does()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_pipeline_");
        using var admin = site.Client();
        using var anonymous = site.Client();
        await site.CompleteSetupAsync(admin);

        static void AssertNoStore(string request, HttpResponseMessage response, HttpStatusCode expected)
        {
            Assert.True(expected == response.StatusCode, $"{request}: {(int)response.StatusCode}");
            Assert.True("no-store" == response.Headers.CacheControl?.ToString(), $"{request}: Cache-Control {response.Headers.CacheControl}");
        }

        AssertNoStore("anonymous GET /api/setup/status", await anonymous.GetAsync("/api/setup/status"), HttpStatusCode.OK);
        AssertNoStore("anonymous GET /api/admin/backups", await anonymous.GetAsync("/api/admin/backups"), HttpStatusCode.Unauthorized);
        // The CSRF check throws before the endpoint; RequestErrorHandling writes the 400.
        AssertNoStore("POST /api/admin/backups/run without CSRF", await admin.PostAsync("/api/admin/backups/run", null), HttpStatusCode.BadRequest);
        AssertNoStore("GET /api/no-such-resource", await admin.GetAsync("/api/no-such-resource"), HttpStatusCode.NotFound);
        AssertNoStore("GET /api/admin/backups", await admin.GetAsync("/api/admin/backups"), HttpStatusCode.OK);
        AssertNoStore("GET /api/secrets/search", await admin.GetAsync("/api/secrets/search"), HttpStatusCode.OK);
    }
}
