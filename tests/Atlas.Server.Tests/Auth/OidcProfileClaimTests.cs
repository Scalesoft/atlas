using System.Security.Claims;
using Atlas.Server.Features.Auth.OpenIdConnect;
using OpenIddict.Abstractions;
using Xunit;

namespace Atlas.Server.Tests.Auth;

public sealed class OidcProfileClaimTests
{
    [Fact]
    public void A_profile_claim_sent_twice_reads_its_first_value_instead_of_refusing_the_login()
    {
        // JetBrains Hub sends the name and the e-mail in the identity token and again in its userinfo (Atlas 3.4.2).
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(OpenIddictConstants.Claims.Name, "Jana Nováková"), new Claim(OpenIddictConstants.Claims.Name, "Jana Nováková"),
            new Claim(OpenIddictConstants.Claims.Email, "jana@example.test"), new Claim(OpenIddictConstants.Claims.Email, "jana@example.test")], "oidc"));
        Assert.Throws<InvalidOperationException>(() => principal.GetClaim(OpenIddictConstants.Claims.Name));
        Assert.Equal("Jana Nováková", OidcEndpoints.FirstClaim(principal, OpenIddictConstants.Claims.Name));
        Assert.Equal("jana@example.test", OidcEndpoints.FirstClaim(principal, OpenIddictConstants.Claims.Email));
        Assert.Null(OidcEndpoints.FirstClaim(principal, OpenIddictConstants.Claims.EmailVerified));
    }
}
