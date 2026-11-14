using Atlas.Server.Features.Vaults.Contracts;

namespace Atlas.Server.Features.Vaults.Commands;

/// <summary>Adds or changes the grant of one user or group on a whole vault (PUT /api/vaults/{id}/grants).</summary>
public sealed record SetVaultGrant(Guid VaultId, UpdateVaultGrant Request);

/// <summary>Closes the grant of one user or group on a whole vault (DELETE /api/vaults/{id}/grants); a missing grant is a no-op.</summary>
public sealed record RevokeVaultGrant(Guid VaultId, Guid? UserId, Guid? GroupId, string? ExpectedVersion);

/// <summary>Adds or changes the grant of one user or group on one secret (PUT /api/secrets/{id}/grants).</summary>
public sealed record SetSecretGrant(Guid SecretId, UpdateVaultGrant Request);

/// <summary>Closes the grant of one user or group on one secret (DELETE /api/secrets/{id}/grants); a missing grant is a no-op.</summary>
public sealed record RevokeSecretGrant(Guid SecretId, Guid? UserId, Guid? GroupId, string? ExpectedVersion);
