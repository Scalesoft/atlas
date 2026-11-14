using Atlas.Server.Features.Vaults.Domain;

namespace Atlas.Server.Features.Vaults.Runtime;

/// <summary>
/// The outcome of <see cref="VaultSnapshots.ReadFieldsAsync"/>: the vault state and, when Fresh and the entry is still in the file,
/// copies of the requested fields as UTF-8 bytes. The holder zeroes <see cref="Values"/> as soon as it has answered the release.
/// </summary>
public sealed record VaultFieldRead(VaultRuntimeState State, IReadOnlyDictionary<VaultSecretFields, byte[]>? Values)
{
    public override string ToString() => $"Vault field read {State} ({(Values is null ? "no values" : "values omitted")})";
}
