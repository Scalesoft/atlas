using Atlas.Server.Features.Security.Contracts;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Security.Networks;
using Atlas.Server.Features.Security.Reauth;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Security.Queries;

/// <summary>Builds <see cref="ProtectionState"/> of one level in a read transaction, under FOR SHARE on the security configuration.
/// Never used after a write in the same transaction: the cache must only learn committed versions.</summary>
public sealed class ProtectionStates
{
    private readonly ISession m_session;
    private readonly IAtlasLocks m_locks;
    private readonly SecurityConfigurationCache m_configuration;
    private readonly FreshAuthMethods m_methods;
    private readonly IHttpContextAccessor m_http;

    public ProtectionStates(ISession session, IAtlasLocks locks, SecurityConfigurationCache configuration, FreshAuthMethods methods,
        IHttpContextAccessor http)
    {
        m_session = session;
        m_locks = locks;
        m_configuration = configuration;
        m_methods = methods;
        m_http = http;
    }

    public async Task<ProtectionState> BuildAsync(UserAccount actor, Vault? vault, VaultSecret? secret, bool canManage, CancellationToken ct)
    {
        var configuration = await m_configuration.CurrentAsync(m_session, m_locks, ct);
        var (instanceSetting, vaultSetting, secretSetting) = await ProtectionReads.CurrentAsync(m_session, vault?.Id, secret?.Id, ct);
        var chain = ProtectionReads.Chain(instanceSetting, vault?.Id, vaultSetting, secret?.Id, secretSetting);
        var own = secret is not null ? secretSetting : vault is not null ? vaultSetting : instanceSetting;
        var views = chain.Levels.Select(level => new ProtectionLevelView(level.Level, level.TargetId,
            level.Level == ProtectionLevel.VaultLevel ? vault!.Name : level.Level == ProtectionLevel.SecretLevel ? secret!.Title : null,
            level.Mode?.Name())).ToArray();
        ProtectionImpactView? impact = null;
        if (canManage)
        {
            var (vaults, secrets) = await ProtectionReads.ImpactAsync(m_session, vault?.Id, secret?.Id, ct);
            impact = new ProtectionImpactView(vaults, secrets);
        }
        var client = configuration.Resolve(ClientAddressEvidence.Of(m_http.HttpContext));
        var network = configuration.MatchNetwork(client.Determined ? client.Address : null);
        var reauth = ProtectionDecision.Requires(chain.Effective, network is not null);
        var method = await m_methods.ChooseAsync(actor, false, ct);
        var support = method?.Support ?? FreshAuthSupport.Unsupported;
        var preview = new ReleasePreviewView(reauth, network is not null && chain.Effective == ProtectionMode.OutsideTrustedNetworks,
            FreshAuthMethod.Name(support), reauth && support == FreshAuthSupport.Unsupported);
        return new ProtectionState(chain.Target.Level, chain.Target.TargetId, own is null ? null : ProtectionModes.FromStored(own.Mode).Name(),
            chain.Effective.Name(), chain.Source.Level, views, canManage, own is null ? null : AdministrationVersion.Format(own.ActiveFrom), impact,
            preview, configuration.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
