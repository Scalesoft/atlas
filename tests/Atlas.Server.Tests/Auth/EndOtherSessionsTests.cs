using System.Net;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Auth;

/// <summary>SEC-A-6: the caller ends every other session of the account and keeps its own (the cookie gets the new stamp).</summary>
public sealed class EndOtherSessionsTests
{
    [PostgreSqlFact]
    public async Task Ending_other_sessions_keeps_this_one()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_end_other_sessions_");
        using var here = site.Client();
        var login = (await site.CompleteSetupAsync(here))["username"]!.GetValue<string>();
        using var laptop = site.Client();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(laptop, HttpMethod.Post, "/api/auth/login",
            new { username = login, password = site.Password }), HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(here, HttpMethod.Post, "/api/profile/sessions/end-others")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/auth/me")).StatusCode);
    }
}
