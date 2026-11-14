using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Atlas.Server.Features.Auth.Domain;

namespace Atlas.Server.Tests;

public sealed class ProfileSessionTests
{
    [Theory]
    [InlineData("oidc:hub", true)]
    [InlineData("builtin", true)]
    [InlineData(null, false)]
    public async Task Refresh_changes_only_the_stamp_and_preserves_original_cookie_authentication(string? method, bool hasTime)
    {
        var user = new UserAccount { Id = Guid.NewGuid(), Username = "reader", SecurityStamp = "new-stamp" };
        var original = SignInPrincipal.Create(user, method ?? "builtin", DateTimeOffset.Parse("2026-09-01T10:00:00Z"));
        var identity = (ClaimsIdentity)original.Identity!;
        identity.RemoveClaim(identity.FindFirst("stamp")!);
        identity.AddClaim(new Claim("stamp", "old-stamp"));
        if (!hasTime)
        {
            identity.RemoveClaim(identity.FindFirst("auth_time")!);
        }
        if (method is null)
        {
            identity.RemoveClaim(identity.FindFirst("authentication_method")!);
        }
        var properties = new AuthenticationProperties { IssuedUtc = DateTimeOffset.Parse("2026-09-01T11:00:00Z"), ExpiresUtc = DateTimeOffset.Parse("2026-09-08T11:00:00Z"), IsPersistent = true };
        var authentication = new CapturingAuthentication(new AuthenticationTicket(original, properties, CookieAuthenticationDefaults.AuthenticationScheme));
        using var services = new ServiceCollection().AddSingleton<IAuthenticationService>(authentication).BuildServiceProvider();
        var context = new DefaultHttpContext { User = original, RequestServices = services };
        await SignInPrincipal.RefreshAsync(context, user);
        Assert.NotNull(authentication.SignedIn);
        Assert.Equal("new-stamp", authentication.SignedIn.FindFirstValue("stamp"));
        Assert.Equal("old-stamp", original.FindFirstValue("stamp"));
        Assert.Equal(original.FindFirstValue("auth_time"), authentication.SignedIn.FindFirstValue("auth_time"));
        Assert.Equal(method, authentication.SignedIn.FindFirstValue("authentication_method"));
        Assert.Same(properties, authentication.Properties);
        Assert.False(SignInPrincipal.HasRecentAuthentication(authentication.SignedIn, DateTimeOffset.Parse("2026-09-07T10:00:00Z")));
    }

    private sealed class CapturingAuthentication(AuthenticationTicket ticket) : IAuthenticationService
    {
        public ClaimsPrincipal? SignedIn { get; private set; }
        public AuthenticationProperties? Properties { get; private set; }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.Success(ticket));
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, scheme);
            SignedIn = principal;
            Properties = properties;
            return Task.CompletedTask;
        }
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }
}
