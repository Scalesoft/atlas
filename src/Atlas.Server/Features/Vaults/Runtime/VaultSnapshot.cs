using Atlas.Server.Features.Vaults.Kdbx;

namespace Atlas.Server.Features.Vaults.Runtime;

/// <summary>
/// What this process knows about a vault after its last check. <see cref="StateVersion"/> grows with every
/// transition of that vault, so a result computed before a password change can be recognized as stale.
/// <see cref="Entries"/> is empty unless the state is <see cref="VaultRuntimeState.Fresh"/>: an unavailable file
/// never serves the previous content.
/// </summary>
public sealed record VaultSnapshot(
    Guid VaultId,
    VaultRuntimeState State,
    long StateVersion,
    IReadOnlyList<VaultSnapshotEntry> Entries,
    byte[]? Sha256,
    KdbxFailure? KdbxFailure,
    string? Failure,
    DateTime CheckedAt)
{
    public override string ToString() => $"Vault {VaultId:N} snapshot {State} v{StateVersion}";
}
