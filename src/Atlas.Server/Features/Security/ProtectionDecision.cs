using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Security.Networks;
using Atlas.Server.Features.Security.Reauth;

namespace Atlas.Server.Features.Security;

/// <summary>
/// Whether releasing a value needs fresh re-authentication now, and why: the effective mode and its inheritance chain, the verified
/// client address with how it was derived, the trusted network that matched (null for none or an unclear origin), whether that
/// network waives re-authentication, and whether the account can re-authenticate at all. Blocked is the "nepodporováno" state:
/// re-authentication is required and the account's method cannot prove it, so nothing may be released. ConfigurationVersion is
/// the version the decision used. The access level (Čte) and the audit are the caller's; a network exception never replaces them.
/// The V5 contract (<see cref="Vaults.Release.ReleaseDecision"/>) is a projection of it.
/// </summary>
public sealed record ProtectionDecision(Guid VaultId, Guid? SecretId, ProtectionChain Chain, ClientAddress Client, TrustedRule? Network,
    bool NetworkException, bool ReauthRequired, FreshAuthMethod? Method, long ConfigurationVersion)
{
    public ProtectionMode EffectiveMode => Chain.Effective;

    public FreshAuthSupport Support => Method?.Support ?? FreshAuthSupport.Unsupported;

    public bool Blocked => ReauthRequired && Support == FreshAuthSupport.Unsupported;

    /// <summary>The rule of the three modes (spec 5.1): Always always, OutsideTrustedNetworks unless a verified client address is in an
    /// active trusted network, Never never.</summary>
    public static bool Requires(ProtectionMode mode, bool trustedNetwork) => mode switch
    {
        ProtectionMode.Never => false,
        ProtectionMode.OutsideTrustedNetworks => !trustedNetwork,
        _ => true
    };
}
