using Atlas.Server.Features.Auth.Commands;
using Atlas.Server.Features.Auth.Domain;
using Wolverine;

namespace Atlas.Server.Features.Auth;

public sealed class CookieSignInService : ISignInService
{
    private readonly IMessageBus m_bus;

    public CookieSignInService(IMessageBus bus)
    {
        m_bus = bus;
    }

    public async Task SignInAsync(HttpContext context, UserAccount user, string method = "builtin")
    {
        if (!user.IsActive)
        {
            throw new ApiException(401, "Přihlášení vypršelo.");
        }
        var authenticatedAt = await m_bus.InvokeAsync<DateTime>(new RecordSignIn(user.Id, user.SecurityStamp, method), context.RequestAborted);
        await SignInPrincipal.IssueAsync(context, user, method, authenticatedAt);
    }

    public Task RefreshSignInAsync(HttpContext context, UserAccount user) => SignInPrincipal.RefreshAsync(context, user);
}
