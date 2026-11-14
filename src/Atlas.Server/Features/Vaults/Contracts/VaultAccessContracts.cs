namespace Atlas.Server.Features.Vaults.Contracts;

/// <summary>One reason a person or group has a level on a vault or secret. Kind: wikiAdmin (manage on every vault and secret),
/// spaceAdmin (a space Admin grant of the vault's space: manage on all its vaults and secrets), vault (a grant on the whole vault) or
/// secret (a grant on this secret). Level:
/// discover, read or manage. GroupId and Name name the group the grant reaches the person through; Name is empty for a direct
/// grant. Version is the CAS token of a vault or secret grant (its ActiveFrom) for changing or revoking it. Effective is false for
/// a current grant outside its validity (not started yet or expired), listed so a manager can change it.</summary>
public sealed record VaultAccessSource(string Kind, string Name, string Level, Guid? GroupId = null, Guid? GrantId = null,
    DateTime? StartAt = null, DateTime? ExpiresAt = null, string? Version = null, bool Effective = true);

/// <summary>An active person with a source; Level is the highest effective source (null: none).</summary>
public sealed record VaultAccessPerson(Guid UserId, string DisplayName, string Username, string? Level, IReadOnlyList<VaultAccessSource> Sources);

/// <summary>A group with a grant that applies to the target; MemberCount counts its current active members.</summary>
public sealed record VaultAccessGroup(Guid GroupId, string Name, int MemberCount, string? Level, IReadOnlyList<VaultAccessSource> Sources);

/// <summary>The last change of a grant on the target (resource_grant.added or resource_grant.removed) and who made it.</summary>
public sealed record VaultAccessChange(DateTime OccurredAt, string Action, Guid? ActorId, string? ActorName);

/// <summary>
/// Who may do what with a vault or secret and why ("proč má přístup"). Everyone who can Discover the target gets MyAccess, their
/// own row with its sources. People, Groups and LastChange are management detail, present only with manage (CanManage); people
/// are limited to the first 500 (HasMore). A secret shows the space Admin role, the vault grants and its own grants; a vault
/// shows the space Admin role and the vault grants.
/// </summary>
public sealed record VaultAccessState(Guid SpaceId, string SpaceName, Guid VaultId, string VaultName, Guid? SecretId, string? SecretTitle,
    bool CanManage, VaultAccessPerson MyAccess, IReadOnlyList<VaultAccessPerson> People, bool HasMore, IReadOnlyList<VaultAccessGroup> Groups,
    VaultAccessChange? LastChange);

/// <summary>Adds or changes the grant of exactly one user or group on a vault or secret: Level discover, read or manage.
/// ExpectedVersion is the current grant's Version, null when there is none. StartAt/ExpiresAt are business validity.</summary>
public sealed record UpdateVaultGrant(Guid? UserId, Guid? GroupId, string? Level, string? ExpectedVersion,
    DateTimeOffset? StartAt = null, DateTimeOffset? ExpiresAt = null);

/// <summary>One vault the caller has a level on. Level is the vault-wide level (space Admin role or vault grants; discover when
/// only secret grants reach it); SecretGrants tells that grants on individual secrets reach further into the vault.</summary>
public sealed record VaultAccessSummary(Guid VaultId, Guid SpaceId, string Level, bool SecretGrants);

/// <summary>
/// GET /api/security/me, what the UI may offer; the server still authorizes every call. GlobalSecurityAdmin: networks, proxies
/// and the default protection mode (every WikiAdmin, owner decision 4). ManagesAllVaults: a WikiAdmin manages every vault and
/// secret, so VaultManagerSpaces does not list every space for them (owner decision 27. 9.); for everyone else VaultManagerSpaces
/// are the spaces whose vaults the caller manages as a space Admin. Vaults: current vaults the caller can at least Discover.
/// FreshAuth: whether the caller can prove a fresh re-authentication: supported (the local password, or a provider seen sending a
/// fresh auth_time), unknown (a provider not verified yet) or unsupported (no such method; anything that requires it is refused).
/// FreshAuthMethod: password or oidc, null without one. ReauthWindowExpiresAt: until when security changes of this session need no new
/// re-authentication (ten minutes after the last one); null when no window is open. Value releases always ask again.
/// </summary>
public sealed record SecurityCapabilitiesView(bool GlobalSecurityAdmin, bool ManagesAllVaults, IReadOnlyList<Guid> VaultManagerSpaces,
    IReadOnlyList<VaultAccessSummary> Vaults, string FreshAuth, string? FreshAuthMethod, DateTime? ReauthWindowExpiresAt);
