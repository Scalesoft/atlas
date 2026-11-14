using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Atlas.Server.Features.Auth.Domain;

/// <summary>
/// The cookie side of signing in: claims, freshness and re-issuing. Pure with respect to the
/// database, so it stays testable without a host and reusable from the OIDC callback.
/// </summary>
public static class SignInPrincipal
{
    private const int RecentAuthenticationSeconds = 300;

    public static ClaimsPrincipal Create(UserAccount user, string method, DateTimeOffset authenticatedAt) => new(new ClaimsIdentity(
        new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("stamp", user.SecurityStamp),
            new Claim("atlas_session", Guid.NewGuid().ToString("N")),
            new Claim("authentication_method", method),
            new Claim("auth_time", authenticatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
        }, CookieAuthenticationDefaults.AuthenticationScheme));

    public static bool HasRecentAuthentication(ClaimsPrincipal principal, DateTimeOffset now)
    {
        if (principal.Identity?.IsAuthenticated != true || !long.TryParse(principal.FindFirstValue("auth_time"), out var authenticatedAt))
        {
            return false;
        }
        var elapsed = now.ToUnixTimeSeconds() - authenticatedAt;
        return elapsed >= 0 && elapsed <= RecentAuthenticationSeconds;
    }

    public static async Task IssueAsync(HttpContext context, UserAccount user, string method, DateTime authenticatedAt)
    {
        var principal = Create(user, method, new DateTimeOffset(authenticatedAt));
        // Persistent: closing the browser does not end the session either; the sliding window above does.
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { IsPersistent = true });
        context.User = principal;
    }

    public static async Task RefreshAsync(HttpContext context, UserAccount user)
    {
        var authentication = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!user.IsActive || !authentication.Succeeded || authentication.Principal is null
            || authentication.Principal.FindFirstValue(ClaimTypes.NameIdentifier) != user.Id.ToString())
        {
            throw new ApiException(401, "Přihlášení vypršelo.");
        }
        var principal = new ClaimsPrincipal(authentication.Principal.Identities.Select(identity => identity.Clone()));
        var identity = (ClaimsIdentity)principal.Identity!;
        // Only the stamp changes; the original authentication time and method stay as issued.
        foreach (var claim in identity.FindAll("stamp").ToArray())
        {
            identity.RemoveClaim(claim);
        }
        identity.AddClaim(new Claim("stamp", user.SecurityStamp));
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authentication.Properties);
        context.User = principal;
    }
}
