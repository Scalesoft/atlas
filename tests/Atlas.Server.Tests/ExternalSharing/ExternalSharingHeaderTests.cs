using System.Net;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.ExternalSharing;

public sealed class ExternalSharingHeaderTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Public_errors_and_rate_limit_rejections_keep_capability_privacy_headers()
    {
        await using var fixture = await SharingSite.CreateAsync();
        using var invalid = await fixture.Anonymous.GetAsync("/api/shared/invalid");
        Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
        AssertPrivate(invalid);

        // Invalid credentials exercise the real anonymous route limiter without touching content.
        // Bound the attempts; this must reject a burst rather than relying on a delay or a retry loop.
        var rejected = false;
        for (var attempt = 0; attempt < 150; attempt++)
        {
            using var response = await fixture.Anonymous.GetAsync("/api/shared/invalid");
            AssertPrivate(response);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = true;
                break;
            }
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        Assert.True(rejected, "The public route must rate-limit an anonymous burst.");
    }

    private static void AssertPrivate(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
        // The document policy belongs to the /s/* HTML shell, not to the JSON API behind it.
        Assert.False(response.Headers.Contains("Content-Security-Policy-Report-Only"));
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }
}
