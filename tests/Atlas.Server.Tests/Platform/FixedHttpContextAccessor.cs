using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Atlas.Server.Tests.Platform;

/// <summary>
/// Always answers with the same context, so a handler test can run the real <see cref="HttpCurrentActor"/>
/// without depending on the ambient flow an HttpContextAccessor needs.
/// </summary>
internal sealed class FixedHttpContextAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; }

    public void SignIn(Guid userId, string securityStamp)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("stamp", securityStamp)
        };
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
    }
}
