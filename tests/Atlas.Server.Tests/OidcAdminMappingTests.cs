using System.Text.Json;
using System.Security.Claims;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class OidcAdminMappingTests
{
    [Theory]
    [InlineData("groups", "atlas-admins", true)]
    [InlineData("groups", "atlas-admins-extra", false)]
    [InlineData("Groups", "atlas-admins", false)]
    [InlineData("groups", "Atlas-Admins", false)]
    public void Mapping_requires_exact_claim_and_value(string type, string value, bool expected)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(type, value), new Claim("groups", "staff")], "oidc"));
        Assert.Equal(expected, OidcAdminMapping.Matches(principal, [new("groups", "atlas-admins")]));
        Assert.False(OidcAdminMapping.Matches(new ClaimsPrincipal(new ClaimsIdentity(principal.Claims)), [new("groups", "atlas-admins")]));
    }

    [Fact]
    public void Duplicate_and_oversized_mapping_lists_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => OidcAdminMapping.Validate([new("groups", "a"), new("groups", "a")]));
        Assert.Throws<InvalidOperationException>(() => OidcAdminMapping.Validate(Enumerable.Range(0, 21).Select(i => new OidcAdminMapping("groups", i.ToString())).ToArray()));
    }

    [Theory]
    [InlineData("", "admins")]
    [InlineData("groups", "")]
    [InlineData("oi_reg_id", "hub")]
    public void Invalid_admin_mapping_is_rejected(string type, string value)
    {
        var provider = JsonSerializer.Deserialize<AtlasOidcProvider>(JsonSerializer.Serialize(new
        {
            Id = "hub", DisplayName = "Hub", Issuer = "https://hub.example", ClientId = "atlas",
            AdminMappings = new[] { new { ClaimType = type, ClaimValue = value } }
        }))!;
        var options = new AtlasOidcOptions { PublicOrigin = "https://atlas.example", Providers = [provider] };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
