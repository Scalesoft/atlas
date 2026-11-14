using Microsoft.AspNetCore.Identity;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Auth.Builtin;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.OpenIdConnect.Commands;

public sealed class AuthorizeIdentityLinkHandler
{
    private const int PasswordMaxLength = 1024;
    private readonly ISession m_session;
    private readonly ICurrentActor m_actor;
    private readonly IPasswordHasher<UserAccount> m_hasher;
    private readonly AtlasOidcOptions m_settings;
    private readonly BuiltinLoginThrottle m_throttle;

    public AuthorizeIdentityLinkHandler(ISession session, ICurrentActor actor, IPasswordHasher<UserAccount> hasher, AtlasOidcOptions settings,
        BuiltinLoginThrottle throttle)
    {
        m_session = session;
        m_actor = actor;
        m_hasher = hasher;
        m_settings = settings;
        m_throttle = throttle;
    }

    public async Task<LinkChallenge> Handle(AuthorizeIdentityLink command, CancellationToken ct)
    {
        var user = await m_actor.RequireAsync(ct);
        var credential = m_settings.BuiltinEnabled ? await m_session.GetAsync<BuiltinCredential>(user.Id, ct) : null;
        if (credential?.PasswordHash is not null)
        {
            if (string.IsNullOrEmpty(command.Password) || command.Password.Length > PasswordMaxLength)
            {
                throw new ApiException(403, "Pro propojení ověřte aktuální heslo.");
            }
            using var attempt = m_throttle.Begin(user.Username);
            if (m_hasher.VerifyHashedPassword(user, credential.PasswordHash, command.Password) == PasswordVerificationResult.Failed)
            {
                attempt.Fail();
                throw new ApiException(403, "Pro propojení ověřte aktuální heslo.");
            }
        }
        else if (!command.RecentProviderAuthentication)
        {
            throw new ApiException(403, "Pro propojení se nejprve znovu přihlaste přes svého poskytovatele.");
        }
        return new LinkChallenge(user.Id, user.SecurityStamp);
    }
}
