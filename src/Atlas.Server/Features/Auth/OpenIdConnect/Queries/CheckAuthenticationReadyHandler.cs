using NHibernate.Linq;
using Atlas.Server.Features.Auth.OpenIdConnect.Domain;
using Atlas.Server.Features.Auth.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.OpenIdConnect.Queries;

/// <summary>
/// Readiness of the authentication surface: the database answers and, in OIDC-only mode, at least one
/// active wiki administrator has a linked provider. Reading the Workspace row is also the liveness probe
/// the health endpoint used to make with SELECT 1.
/// </summary>
public sealed class CheckAuthenticationReadyHandler
{
    private readonly ISession m_session;
    private readonly AtlasOidcOptions m_options;

    public CheckAuthenticationReadyHandler(ISession session, AtlasOidcOptions options)
    {
        m_session = session;
        m_options = options;
    }

    public async Task<WorkspaceState> Handle(CheckAuthenticationReady query, CancellationToken ct)
    {
        var workspace = await m_session.GetAsync<Workspace>(1, ct);
        if (!m_options.BuiltinEnabled)
        {
            var issuers = m_options.Providers.Select(p => p.Issuer).ToList();
            var available = await (from identity in m_session.Query<ExternalIdentity>()
                join user in m_session.Query<UserAccount>() on identity.UserId equals user.Id
                where identity.ActiveTo == null && user.IsActive && user.IsWikiAdmin && issuers.Contains(identity.Issuer)
                select user.Id).AnyAsync(ct);
            if (!available)
            {
                throw new ApiException(503, "OIDC-only režim vyžaduje aktivního správce s propojeným poskytovatelem. Zapněte builtin přihlášení, dokončete nastavení a propojte účet správce.");
            }
        }
        return new WorkspaceState(workspace is not null, workspace?.Name);
    }
}
