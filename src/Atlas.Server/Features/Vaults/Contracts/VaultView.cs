namespace Atlas.Server.Features.Vaults.Contracts;

/// <summary>
/// A vault and its status: unlocked, needsPassword (Vyžaduje heslo), unavailable (Nedostupné) or error (Chyba).
/// No password, key or hash of either is ever part of it; SnapshotSha256 is the hash of the encrypted file.
/// Level is the caller's vault-wide level (discover, read, manage; a WikiAdmin manages every vault) or null.
/// </summary>
public sealed record VaultView(Guid Id, Guid SpaceId, string SpaceName, string Name, Guid LocationId, string LocationName, string FileName,
    string Status, string? LastFailure, DateTime? LastRefreshAt, string? SnapshotSha256, int EntryCount, DateTime CreatedAt, string? Level);
