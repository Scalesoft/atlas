using Atlas.Server.Platform.Storage;

namespace Atlas.Server.Features.Vaults.Runtime;

/// <summary>
/// One read of a KDBX file: the SHA-256 of the encrypted bytes, the file metadata before the read and the entry
/// metadata. <see cref="Entries"/> is null when the bytes matched the previous snapshot and were not decrypted.
/// </summary>
public sealed record VaultFileRead(byte[] Sha256, FileStorageMetadata Metadata, IReadOnlyList<VaultSnapshotEntry>? Entries)
{
    public override string ToString() => $"Vault file read ({Entries?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unchanged"} entries)";
}
