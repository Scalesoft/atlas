using Atlas.Server.Features.Security.Domain;

namespace Atlas.Server.Features.Security.Reauth;

/// <summary>
/// The security change guard of WP4 V4: every security change (vault and secret grants, approving an access request, protection
/// modes, trusted networks and proxies, the hop limit) needs a fresh re-authentication, also inside a trusted network (owner decision
/// 27. 9.): a proof for the security operation and the change's scope (<see cref="ReauthTarget.Of"/>), or the ten-minute window of an
/// earlier verification in the same session (owner decision 28. 9.). The change and its reauth.consumed or reauth.window_used audit
/// event share the transaction.
/// </summary>
public sealed class ReauthSecurityChangeGuard : ISecurityChangeGuard
{
    private readonly ReauthProofs m_proofs;

    public ReauthSecurityChangeGuard(ReauthProofs proofs)
    {
        m_proofs = proofs;
    }

    public Task RequireAsync(UserAccount actor, SecurityChange change, CancellationToken ct)
    {
        return m_proofs.RequireSecurityAsync(actor, ReauthTarget.Of(change), ct);
    }
}
