namespace Atlas.Server.Features.Vaults.Audit.Queries;

/// <summary>
/// GET /api/vaults/{id}/audit (every secret of the vault) or /api/secrets/{id}/audit (SecretId set): releases, client reports,
/// access requests and grants of the target, newest first, for Spravuje holders. Kind: release, request or grant. Result: a
/// release result (released, forbidden, not_found, reauth_required, unsupported, unavailable) or a client report (shown, copied).
/// From inclusive, To exclusive. Paging by BeforeId (keyset over the access audit feed), Limit 1–100.
/// </summary>
public sealed record GetVaultAudit(Guid? VaultId, Guid? SecretId, Guid? ActorId, string? Kind, string? Result, DateTimeOffset? From,
    DateTimeOffset? To, string? BeforeId, int Limit);
