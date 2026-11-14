using Atlas.Server.Features.Vaults.Domain;

namespace Atlas.Server.Features.Vaults.Runtime;

/// <summary>Metadata of one current KeePass entry outside the recycle bin. Never carries a value.</summary>
public sealed record VaultSnapshotEntry(Guid Uuid, string Title, string GroupPath, VaultSecretFields Fields);
