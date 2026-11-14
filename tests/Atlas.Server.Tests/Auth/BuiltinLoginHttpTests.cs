using System.Globalization;
using System.Net;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Auth;

public sealed class BuiltinLoginHttpTests
{
    private const int PermitsPerUsername = 10;

    /// <summary>
    /// The throttle budget is per account, not per address: guessing one password must not lock out
    /// everyone else behind the same office IP. Retry-After has to be a number of seconds the client
    /// can parse.
    /// </summary>
    [PostgreSqlFact]
    public async Task Repeated_wrong_passwords_throttle_one_account_and_leave_other_accounts_alone()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_login_throttle_");
        using var admin = site.Client();
        var login = (await site.CompleteSetupAsync(admin))["username"]!.GetValue<string>();
        using var attacker = site.Client();
        // Successful sign-ins never spend the budget (only failed password proofs count).
        for (var success = 0; success < 3; success++)
        {
            var signedIn = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/auth/login", new { username = login, password = site.Password });
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        }
        for (var attempt = 0; attempt < PermitsPerUsername; attempt++)
        {
            var refused = await AtlasHttpSite.SendAsync(attacker, HttpMethod.Post, "/api/auth/login", new { username = login, password = "wrong password here" });
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        var throttled = await AtlasHttpSite.SendAsync(attacker, HttpMethod.Post, "/api/auth/login", new { username = login, password = "wrong password here" });
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        var retryAfter = Assert.Single(throttled.Headers.GetValues("Retry-After"));
        Assert.True(int.TryParse(retryAfter, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds), $"Retry-After was {retryAfter}");
        Assert.InRange(seconds, 1, 60);
        var other = await AtlasHttpSite.SendAsync(attacker, HttpMethod.Post, "/api/auth/login", new { username = TestLogin.For("someone-else"), password = "wrong password here" });
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
    }
}
