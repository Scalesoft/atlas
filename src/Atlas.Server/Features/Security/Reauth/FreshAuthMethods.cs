using System.Security.Claims;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Auth.OpenIdConnect.Domain;
using Atlas.Server.Features.Security.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Security.Reauth;

/// <summary>
/// Which method re-authenticates an account: the builtin password when the module is on and the account has one, otherwise a
/// configured OIDC provider the account has a current linked identity with. The best supported method wins (supported, then
/// unknown, then unsupported); among equals the method of the current sign-in comes first, then the password.
/// </summary>
public sealed class FreshAuthMethods
{
    private readonly ISession m_session;
    private readonly AtlasOidcOptions m_options;
    private readonly IHttpContextAccessor m_http;

    public FreshAuthMethods(ISession session, AtlasOidcOptions options, IHttpContextAccessor http)
    {
        m_session = session;
        m_options = options;
        m_http = http;
    }

    /// <summary>The method to use; null when the account has none (freshAuth unsupported). providersOnly: the self-test, optionally of
    /// one provider.</summary>
    public async Task<FreshAuthMethod?> ChooseAsync(UserAccount actor, bool providersOnly, CancellationToken ct, string? providerId = null)
    {
        var candidates = new List<FreshAuthMethod>();
        if (!providersOnly && m_options.BuiltinEnabled
            && !string.IsNullOrEmpty((await m_session.GetAsync<BuiltinCredential>(actor.Id, ct))?.PasswordHash))
        {
            candidates.Add(new FreshAuthMethod(ReauthChallenge.PasswordMethod, null, null, FreshAuthSupport.Supported));
        }
        if (m_options.Providers.Length > 0)
        {
            var issuers = await m_session.QueryOver<ExternalIdentity>()
                .Where(i => i.UserId == actor.Id && i.ActiveTo == null)
                .Select(i => i.Issuer)
                .ListAsync<string>(ct);
            var linked = m_options.Providers
                .Where(p => issuers.Contains(p.Issuer, StringComparer.Ordinal) && (providerId is null || p.Id == providerId)).ToArray();
            if (linked.Length > 0)
            {
                var ids = linked.Select(p => p.Id).ToArray();
                var statuses = (await m_session.QueryOver<OidcFreshAuthStatus>()
                    .WhereRestrictionOn(s => s.ProviderId).IsIn(ids).ListAsync(ct)).ToDictionary(s => s.ProviderId);
                candidates.AddRange(linked.Select(p => new FreshAuthMethod(ReauthChallenge.OidcMethod, p.Id, p.DisplayName,
                    Support(statuses.GetValueOrDefault(p.Id), p))));
            }
        }
        var session = m_http.HttpContext?.User.FindFirstValue("authentication_method");
        return candidates
            .OrderBy(c => c.Support)
            .ThenBy(c => SessionMethod(c) == session ? 0 : 1)
            .ThenBy(c => c.Method == ReauthChallenge.PasswordMethod ? 0 : 1)
            .FirstOrDefault();
    }

    /// <summary>freshAuth of GET /api/security/me: the support of the chosen method, unsupported without any.</summary>
    public async Task<(FreshAuthSupport Support, FreshAuthMethod? Method)> StateAsync(UserAccount actor, CancellationToken ct)
    {
        var method = await ChooseAsync(actor, false, ct);
        return (method?.Support ?? FreshAuthSupport.Unsupported, method);
    }

    /// <summary>The recorded verdict of a provider; unknown without one or for a changed issuer.</summary>
    public static FreshAuthSupport Support(OidcFreshAuthStatus? status, AtlasOidcProvider provider)
    {
        if (status is null || !string.Equals(status.Issuer, provider.Issuer, StringComparison.Ordinal))
        {
            return FreshAuthSupport.Unknown;
        }
        return status.Verdict == OidcFreshAuthStatus.SupportedVerdict ? FreshAuthSupport.Supported : FreshAuthSupport.Unsupported;
    }

    private static string SessionMethod(FreshAuthMethod method)
    {
        return method.Method == ReauthChallenge.PasswordMethod ? "builtin" : "oidc:" + method.ProviderId;
    }
}
