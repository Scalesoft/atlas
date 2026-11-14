using System.Net;
using System.Net.Http.Json;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Auth;

public sealed class AnonymousApiHttpTests
{
    /// <summary>
    /// RequireAuthorization answers before any handler runs, so the cookie scheme owes the caller the
    /// problem+json body the handlers write; a bare status code would be a contract change.
    /// </summary>
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Authorized_api_groups_answer_401_with_the_problem_body_the_handlers_write()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_anonymous_api_");
        using var anonymous = site.Client();
        string[] paths = ["/api/auth/me", "/api/profile/appearance", $"/api/spaces/{Guid.NewGuid()}/settings"];
        foreach (var path in paths)
        {
            var body = await AtlasHttpSite.Json(await anonymous.GetAsync(path), HttpStatusCode.Unauthorized);
            Assert.Equal("Přihlaste se.", body["title"]!.GetValue<string>());
            Assert.Equal(401, body["status"]!.GetValue<int>());
        }
    }

    /// <summary>
    /// Authorization now runs before the antiforgery check, so a CSRF-less anonymous mutation is
    /// answered 401 instead of the 400 the inline endpoint used to produce. Signed-in callers still
    /// get 400 without a token.
    /// </summary>
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Anonymous_appearance_update_is_401_and_a_signed_in_one_without_CSRF_is_400()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_anonymous_appearance_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var appearance = new { layout = "islands", accent = "blue", mode = "dark" };
        using var anonymous = site.Client();
        var body = await AtlasHttpSite.Json(await anonymous.PutAsJsonAsync("/api/profile/appearance", appearance), HttpStatusCode.Unauthorized);
        Assert.Equal("Přihlaste se.", body["title"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/profile/appearance", appearance)).StatusCode);
        var updated = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/profile/appearance", appearance), HttpStatusCode.OK);
        Assert.Equal("islands", updated["layout"]!.GetValue<string>());
        // A client that predates the reduced-motion preference does not send it and keeps the default.
        Assert.Equal("system", updated["motion"]!.GetValue<string>());
    }
}
