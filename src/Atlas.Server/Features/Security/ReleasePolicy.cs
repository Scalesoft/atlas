using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Security.Networks;
using Atlas.Server.Features.Security.Reauth;
using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Features.Vaults.Release;
using ISession = NHibernate.ISession;
using ReauthChallenge = Atlas.Server.Features.Security.Domain.ReauthChallenge;

namespace Atlas.Server.Features.Security;

/// <summary>
/// The release decision of WP4 V5 (<see cref="IReleasePolicy"/>), in the caller's transaction: FOR SHARE on the security configuration
/// row (one consistent version of modes, networks and proxies), the protection chain of the secret, the client address resolved from
/// raw evidence against the current proxies, and the account's re-authentication method. Proofs for reveal and copy are consumed like
/// security change proofs (<see cref="ReauthProofs"/>), bound to the secret and the operation.
/// </summary>
public sealed class ReleasePolicy : IReleasePolicy
{
    private readonly ISession m_session;
    private readonly IAtlasLocks m_locks;
    private readonly SecurityConfigurationCache m_configuration;
    private readonly FreshAuthMethods m_methods;
    private readonly ReauthProofs m_proofs;

    public ReleasePolicy(ISession session, IAtlasLocks locks, SecurityConfigurationCache configuration, FreshAuthMethods methods, ReauthProofs proofs)
    {
        m_session = session;
        m_locks = locks;
        m_configuration = configuration;
        m_methods = methods;
        m_proofs = proofs;
    }

    public async Task<ReleaseDecision> EvaluateAsync(UserAccount actor, Guid secretId, HttpContext http, CancellationToken ct)
    {
        var secret = await m_session.GetAsync<VaultSecret>(secretId, ct) ?? throw new ApiException(404, "Trezor nebo heslo nebylo nalezeno.");
        var decision = await DecideAsync(actor, secret.VaultId, secret.Id, ClientContext.From(http), ct);
        return new ReleaseDecision(decision.EffectiveMode.Name(), decision.Chain.Source.Level, decision.ReauthRequired,
            decision.Support != FreshAuthSupport.Unsupported, decision.NetworkException ? decision.Network!.Name : null);
    }

    public Task<bool> TryConsumeProofAsync(UserAccount actor, HttpContext http, Guid secretId, string operation, Guid proofId, CancellationToken ct)
    {
        if (operation is not (ReauthChallenge.RevealOperation or ReauthChallenge.CopyOperation))
        {
            return Task.FromResult(false);
        }
        return m_proofs.TryConsumeAsync(actor, ReauthProofs.SessionOf(http), operation, ReauthTarget.Secret(secretId), proofId, ct);
    }

    /// <summary>The full decision for a vault (no secret: what its secrets inherit) or a secret, with the chain, client and rule.</summary>
    public async Task<ProtectionDecision> DecideAsync(UserAccount actor, Guid vaultId, Guid? secretId, ClientContext client, CancellationToken ct)
    {
        var configuration = await m_configuration.CurrentAsync(m_session, m_locks, ct);
        var chain = await ProtectionReads.ChainAsync(m_session, vaultId, secretId, ct);
        var address = configuration.Resolve(client.Evidence);
        var network = configuration.MatchNetwork(address.Determined ? address.Address : null);
        var reauth = ProtectionDecision.Requires(chain.Effective, network is not null);
        var exception = network is not null && chain.Effective == ProtectionMode.OutsideTrustedNetworks;
        var method = await m_methods.ChooseAsync(actor, false, ct);
        return new ProtectionDecision(vaultId, secretId, chain, address, network, exception, reauth, method, configuration.Version);
    }
}
