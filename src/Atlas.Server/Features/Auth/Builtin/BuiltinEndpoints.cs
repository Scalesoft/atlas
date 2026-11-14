using Atlas.Server.Features.Auth.Builtin.Commands;
using Atlas.Server.Features.Auth.Contracts;
using Atlas.Server.Features.Auth.Queries;
using Wolverine;

namespace Atlas.Server.Features.Auth.Builtin;

public static class BuiltinEndpoints
{
    public static void MapBuiltin(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<BuiltinAuthenticationOptions>().Enabled)
        {
            return;
        }
        app.MapPost("/api/auth/login", LoginAsync).RequireRateLimiting("authentication").Produces<UserDto>();
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request, IMessageBus bus, ISignInService signIn, BuiltinLoginThrottle throttle,
        HttpContext context, CancellationToken ct)
    {
        // The throttle keys are the normalized username and the verified client address (UseAtlasForwardedHeaders), and Retry-After is
        // an HTTP header, so normalization and throttling stay in the endpoint, ahead of any database work.
        var username = Rules.Required(request.Username, "Uživatelské jméno", 100).ToLowerInvariant();
        if (request.Password is null || request.Password.Length > 256)
        {
            throw new ApiException(401, "Neplatné přihlašovací údaje.");
        }
        using var attempt = throttle.TryBegin(username, context.Connection.RemoteIpAddress?.ToString() ?? "unknown", out var retryAfter);
        if (attempt is null)
        {
            context.Response.Headers.RetryAfter = BuiltinLoginThrottle.RetryAfterHeader(retryAfter);
            throw new ApiException(429, "Příliš mnoho pokusů o přihlášení k tomuto účtu. Zkuste to za chvíli.");
        }
        UserAccount user;
        try
        {
            user = await bus.InvokeAsync<UserAccount>(new BuiltinLogin(username, request.Password), ct);
        }
        catch (ApiException refused) when (refused.Status == 401)
        {
            // Only a refused password (or an unknown account) spends the budgets; a successful sign-in never does.
            attempt.Fail();
            throw;
        }
        await signIn.SignInAsync(context, user);
        return Results.Ok(await bus.InvokeAsync<UserDto>(new GetCurrentUser(), ct));
    }
}
