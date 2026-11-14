using System.Net;
using System.Text.Json;
using NHibernate.Linq;
using Atlas.Server.Features.Security.Domain;
using Xunit;

namespace Atlas.Server.Tests.Security;

/// <summary>
/// Fresh re-authentication through the real OpenIddict client against the fake provider (OidcFixture): the challenge parameters,
/// the id_token auth_time read from the validated backchannel token, the binding to the cookie session, and a callback that never
/// signs in. The deployed Hub sends auth_time (verified 27. 9. 2026); this suite is the protocol check.
/// </summary>
public sealed class OidcReauthTests
{
    private static bool SignsIn(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));

    [SearchPostgresFact]
    public async Task Fresh_auth_time_verifies_without_signing_in_and_forces_the_login_form()
    {
        await using var fixture = await OidcFixture.Create(linkAdmin: true);
        var cookie = await fixture.SignIn(0);
        var (started, flow) = await fixture.StartReauth(cookie);
        Assert.Equal(("oidc", "test"), (started.GetProperty("method").GetString(), started.GetProperty("providerId").GetString()));
        Assert.Equal(("login", "0", "required"), (flow.Query["prompt"], flow.Query["max_age"], flow.Query["request_credentials"]));
        var id = started.GetProperty("id").GetString();
        fixture.Provider.AuthTime = DateTimeOffset.UtcNow;
        var callback = await fixture.Complete(flow);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/settings", callback.Headers.Location?.OriginalString);
        Assert.False(SignsIn(callback));
        var status = await fixture.Get($"/api/security/reauth/{id}", cookie);
        Assert.Equal("verified", status.GetProperty("state").GetString());
        var providers = await fixture.Get("/api/security/reauth/self-test", cookie);
        Assert.Equal(("supported", false), (providers[0].GetProperty("verdict").GetString(), providers[0].GetProperty("selfTest").GetBoolean()));
        var me = await fixture.Get("/api/security/me", cookie);
        Assert.Equal("supported", me.GetProperty("freshAuth").GetString());
        // The state token is single use: the same callback again verifies nothing more.
        var replay = await fixture.Complete(flow);
        Assert.Equal("/?authError=external_login_failed", replay.Headers.Location?.OriginalString);
        using var session = fixture.Database.OpenSession();
        Assert.Single(await session.Query<Features.Auth.OpenIdConnect.Domain.ExternalIdentity>().ToListAsync());
        Assert.Equal(1, await session.Query<UserAccount>().CountAsync());
    }

    [SearchPostgresFact]
    public async Task Missing_or_stale_auth_time_is_refused_and_marks_the_provider_unsupported()
    {
        await using var fixture = await OidcFixture.Create(linkAdmin: true);
        var cookie = await fixture.SignIn(0);
        var (started, flow) = await fixture.StartReauth(cookie);
        fixture.Provider.AuthTime = null;
        Assert.False(SignsIn(await fixture.Complete(flow)));
        var status = await fixture.Get($"/api/security/reauth/{started.GetProperty("id").GetString()}", cookie);
        Assert.Equal(("failed", "auth_time_missing"), (status.GetProperty("state").GetString(), status.GetProperty("failure").GetString()));
        Assert.Equal("unsupported", (await fixture.Get("/api/security/me", cookie)).GetProperty("freshAuth").GetString());

        // The WikiAdmin self-test still runs for an unsupported provider; a stale auth_time (an SSO return) is refused too.
        (started, flow) = await fixture.StartReauth(cookie, "/api/security/reauth/self-test", new { providerId = "test", returnUrl = "/settings" });
        fixture.Provider.AuthTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        await fixture.Complete(flow);
        status = await fixture.Get($"/api/security/reauth/{started.GetProperty("id").GetString()}", cookie);
        Assert.Equal(("failed", "auth_time_stale"), (status.GetProperty("state").GetString(), status.GetProperty("failure").GetString()));
        var provider = (await fixture.Get("/api/security/reauth/self-test", cookie))[0];
        Assert.Equal(("unsupported", true, false, true), (provider.GetProperty("verdict").GetString(), provider.GetProperty("authTimePresent").GetBoolean(),
            provider.GetProperty("authTimeFresh").GetBoolean(), provider.GetProperty("selfTest").GetBoolean()));

        (started, flow) = await fixture.StartReauth(cookie, "/api/security/reauth/self-test", new { providerId = "test" });
        fixture.Provider.AuthTime = DateTimeOffset.UtcNow;
        await fixture.Complete(flow);
        Assert.Equal("supported", (await fixture.Get("/api/security/reauth/self-test", cookie))[0].GetProperty("verdict").GetString());
        using var session = fixture.Database.OpenSession();
        Assert.Equal(OidcFreshAuthStatus.SupportedVerdict, (await session.GetAsync<OidcFreshAuthStatus>("test")).Verdict);
    }

    [SearchPostgresFact]
    public async Task Callback_in_another_session_or_after_expiry_verifies_nothing()
    {
        await using var fixture = await OidcFixture.Create(linkAdmin: true);
        var cookie = await fixture.SignIn(0);
        var (started, flow) = await fixture.StartReauth(cookie);
        var other = await fixture.SignIn(0);
        fixture.Provider.AuthTime = DateTimeOffset.UtcNow;
        var swapped = await fixture.Complete(flow with { Cookie = flow.Cookie.Replace(cookie, other, StringComparison.Ordinal) });
        Assert.False(SignsIn(swapped));
        Assert.Equal("pending", (await fixture.Get($"/api/security/reauth/{started.GetProperty("id").GetString()}", cookie)).GetProperty("state").GetString());

        (started, flow) = await fixture.StartReauth(cookie);
        var id = Guid.Parse(started.GetProperty("id").GetString()!);
        using (var session = fixture.Database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            await session.CreateSQLQuery("UPDATE \"ReauthChallenge\" SET \"IssuedAt\"=\"IssuedAt\"-interval '6 minutes', \"ExpiresAt\"=\"ExpiresAt\"-interval '6 minutes' WHERE \"Id\"=:id")
                .SetParameter("id", id).ExecuteUpdateAsync();
            await tx.CommitAsync();
        }
        await fixture.Complete(flow);
        Assert.Equal("expired", (await fixture.Get($"/api/security/reauth/{id}", cookie)).GetProperty("state").GetString());
    }

    [SearchPostgresFact]
    public async Task An_unlinked_provider_account_has_no_way_to_prove_freshness()
    {
        await using var fixture = await OidcFixture.Create();
        var cookie = await fixture.SignIn(0);
        var me = await fixture.Get("/api/security/me", cookie);
        Assert.Equal(JsonValueKind.Null, me.GetProperty("freshAuthMethod").ValueKind);
        Assert.Equal("unsupported", me.GetProperty("freshAuth").GetString());
    }
}
