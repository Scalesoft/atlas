using System.Security.Claims;

namespace Atlas.Server.Features.Auth.OpenIdConnect;

public sealed record OidcAdminMapping(string ClaimType, string ClaimValue)
{
    public static void Validate(OidcAdminMapping[] mappings)
    {
        if (mappings.Length > 20 || mappings.Distinct().Count() != mappings.Length
            || mappings.Any(m => m is null || string.IsNullOrWhiteSpace(m.ClaimType) || m.ClaimType.Length > 200
                || m.ClaimType != m.ClaimType.Trim() || m.ClaimType.StartsWith("oi_", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(m.ClaimValue) || m.ClaimValue.Length > 500 || m.ClaimValue != m.ClaimValue.Trim()))
        {
            throw new InvalidOperationException("Invalid administrator claim mapping.");
        }
    }

    public static bool Matches(ClaimsPrincipal principal, IEnumerable<OidcAdminMapping> mappings)
        => principal.Identity?.IsAuthenticated == true && mappings.Any(mapping => principal.Claims.Any(claim =>
            string.Equals(claim.Type, mapping.ClaimType, StringComparison.Ordinal)
            && string.Equals(claim.Value, mapping.ClaimValue, StringComparison.Ordinal)));
}
