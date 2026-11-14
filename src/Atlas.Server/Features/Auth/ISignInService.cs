namespace Atlas.Server.Features.Auth;

/// <summary>
/// Cookie sign-in is not a message: it owns the HTTP response. The database part travels as
/// <see cref="Commands.RecordSignIn"/> so it runs in its own scope and transaction. Claim shaping
/// and freshness stay static on <see cref="Domain.SignInPrincipal"/>; they need no services.
/// </summary>
public interface ISignInService
{
    Task SignInAsync(HttpContext context, UserAccount user, string method = "builtin");

    Task RefreshSignInAsync(HttpContext context, UserAccount user);
}
