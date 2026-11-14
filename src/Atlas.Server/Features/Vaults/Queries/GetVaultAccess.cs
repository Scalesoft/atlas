namespace Atlas.Server.Features.Vaults.Queries;

/// <summary>Who may do what with a vault and why (GET /api/vaults/{id}/access).</summary>
public sealed record GetVaultAccess(Guid VaultId);

/// <summary>Who may do what with a secret and why (GET /api/secrets/{id}/access).</summary>
public sealed record GetSecretAccess(Guid SecretId);

/// <summary>Users and groups a manager of the vault may grant access to (GET /api/vaults/{id}/grant-candidates?q=).</summary>
public sealed record GetVaultGrantCandidates(Guid VaultId, string? Query);

/// <summary>Users and groups a manager of the secret may grant access to (GET /api/secrets/{id}/grant-candidates?q=).</summary>
public sealed record GetSecretGrantCandidates(Guid SecretId, string? Query);

/// <summary>The caller's security capabilities for the UI (GET /api/security/me).</summary>
public sealed record GetSecurityCapabilities;
