using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Atlas.Server.Features.Administration.Commands;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Queries;
using Wolverine;

namespace Atlas.Server.Features.Administration;

public static class ProfileAccountEndpoints
{
    public static void MapProfileAccount(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/profile/account").RequireAuthorization(new AuthorizeAttribute
        {
            AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme
        });
        group.MapGet("", async (HttpContext context, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<ProfileAccount>(
                new GetProfileAccount(context.User.FindFirstValue("auth_time"), context.User.FindFirstValue("authentication_method")), ct)));
        group.MapPut("", async (UpdateOwnAccount request, HttpContext context, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<ProfileAccount>(
                new SaveProfileAccount(context.User.FindFirstValue("auth_time"), context.User.FindFirstValue("authentication_method"), request), ct)));
    }
}
