namespace Atlas.Server.Features.Vaults.Contracts;

/// <summary>Registry metadata of one KeePass entry: never a value. AvailableFields names the non-empty fields (userName, password,
/// url, notes). Level is the caller's level on this secret (null for a removed entry, listed only to vault managers).</summary>
public sealed record VaultSecretView(Guid SecretId, Guid EntryUuid, string Title, string GroupPath, IReadOnlyList<string> AvailableFields,
    DateTime FirstSeenAt, DateTime? RemovedAt, string? Level);
