namespace Atlas.Server.Features.Vaults.Contracts;

/// <summary>
/// Result of reading the file now: entries outside the recycle bin, SHA-256 of the encrypted file, and registered
/// secrets whose entries are no longer in the file (documents that reference them would break).
/// </summary>
public sealed record VaultVerification(VaultView Vault, int? EntryCount, string? SnapshotSha256, IReadOnlyList<VaultSecretView> Missing);
