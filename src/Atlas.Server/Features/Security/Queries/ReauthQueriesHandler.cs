using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Auth.Persistence;
using Atlas.Server.Features.Security.Contracts;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Security.Reauth;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Security.Queries;

/// <summary>The state of the caller's own challenge (404 for anything of another account or session), and the fresh-auth evidence of
/// every configured provider for the WikiAdmin self-test.</summary>
public sealed class ReauthQueriesHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_content;
    private readonly ReauthProofs m_proofs;
    private readonly AtlasOidcOptions m_options;

    public ReauthQueriesHandler(ISession session, IContentAccess content, ReauthProofs proofs, AtlasOidcOptions options)
    {
        m_session = session;
        m_content = content;
        m_proofs = proofs;
        m_options = options;
    }

    public async Task<ReauthStatusView> Handle(GetReauthStatus query, CancellationToken ct)
    {
        var actor = await m_content.LockActorAsync(null, ct);
        var challenge = await m_session.GetAsync<ReauthChallenge>(query.ChallengeId, ct);
        if (challenge is null || !challenge.BelongsTo(actor, m_proofs.SessionId))
        {
            throw new ApiException(404, "Ověření nebylo nalezeno.");
        }
        var now = await DatabaseClock.NowAsync(m_session, ct);
        return new ReauthStatusView(challenge.Id, challenge.Operation, challenge.TargetKind, challenge.TargetId, challenge.Method,
            challenge.State(now), challenge.Failure, challenge.ExpiresAt, challenge.VerifiedAt + ReauthChallenge.ProofLifetime, challenge.WindowEnd());
    }

    public async Task<IReadOnlyList<FreshAuthProviderView>> Handle(GetFreshAuthProviders query, CancellationToken ct)
    {
        var actor = await m_content.LockActorAsync(null, ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            throw new ApiException(403, "Ověření poskytovatele spouští správce wiki.");
        }
        var statuses = (await m_session.QueryOver<OidcFreshAuthStatus>().ListAsync(ct)).ToDictionary(s => s.ProviderId);
        return m_options.Providers.Select(provider =>
        {
            var status = statuses.GetValueOrDefault(provider.Id);
            var support = FreshAuthMethods.Support(status, provider);
            var known = support != FreshAuthSupport.Unknown;
            return new FreshAuthProviderView(provider.Id, provider.DisplayName, FreshAuthMethod.Name(support),
                known ? status!.AuthTimePresent : null, known ? status!.AuthTimeFresh : null, known ? status!.CheckedAt : null,
                known ? status!.SelfTest : null);
        }).ToArray();
    }
}
