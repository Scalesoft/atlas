namespace Atlas.Server.Features.Security.Contracts;

/// <summary>One level of the inheritance chain: instance, vault or secret, its name, and its explicit mode (null: inherits).</summary>
public sealed record ProtectionLevelView(string Level, Guid? TargetId, string? Name, string? Mode);

/// <summary>How many vaults and current secrets take their effective mode from this level (they are what a change affects).</summary>
public sealed record ProtectionImpactView(int Vaults, int Secrets);

/// <summary>What a release by the caller would need right now under the effective mode: fresh re-authentication, whether a trusted
/// network waives it (only in outsideTrustedNetworks), the caller's freshAuth (supported, unknown, unsupported) and Blocked, the
/// "nepodporováno" state in which nothing is released.</summary>
public sealed record ReleasePreviewView(bool ReauthRequired, bool NetworkException, string FreshAuth, bool Blocked);

/// <summary>
/// The protection of one level (GET/PUT /api/security/protection, /api/vaults/{id}/protection, /api/secrets/{id}/protection).
/// Explicit is the level's own mode (null: inherits), Effective the nearest explicit mode and Source the level it comes from; Chain
/// lists the levels from the instance down. Version is the CAS token of the level's own setting (null while it inherits); Impact
/// is present for managers. ConfigurationVersion is the security configuration version the answer was read at.
/// </summary>
public sealed record ProtectionState(string Level, Guid? TargetId, string? Explicit, string Effective, string Source,
    IReadOnlyList<ProtectionLevelView> Chain, bool CanManage, string? Version, ProtectionImpactView? Impact, ReleasePreviewView Preview,
    string ConfigurationVersion);

/// <summary>Mode: never, outsideTrustedNetworks or always; null inherits (not allowed for the instance). ExpectedVersion is the
/// current Version, null when the level inherits. Requires a re-authentication proof in the X-Atlas-Reauth header.</summary>
public sealed record UpdateProtection(string? Mode, string? ExpectedVersion);

/// <summary>One vault or secret with its own protection mode (GET /api/security/protection/overrides, WikiAdmin): what does not
/// follow the instance default. SecretId and SecretTitle are null for a vault's own mode.</summary>
public sealed record ProtectionOverrideView(Guid SpaceId, string SpaceName, Guid VaultId, string VaultName, Guid? SecretId, string? SecretTitle,
    string Mode);
