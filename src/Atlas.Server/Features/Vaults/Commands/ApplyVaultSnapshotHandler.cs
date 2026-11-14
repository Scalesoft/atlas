using NHibernate;
using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Features.Vaults.Runtime;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Commands;

public sealed class ApplyVaultSnapshotHandler
{
    private readonly ISession m_session;
    private readonly VaultSnapshots m_snapshots;

    public ApplyVaultSnapshotHandler(ISession session, VaultSnapshots snapshots)
    {
        m_session = session;
        m_snapshots = snapshots;
    }

    /// <summary>
    /// Fresh: unlocked, registry synced when the file bytes changed. Unavailable: the state stays and LastFailure
    /// explains it; the registry is left alone, so a missing share never marks entries removed. KeyChanged and
    /// Locked: needs the password. Error: damaged or unsupported file.
    /// </summary>
    public async Task Handle(ApplyVaultSnapshot command, CancellationToken ct)
    {
        var vault = await m_session.GetAsync<Vault>(command.VaultId, LockMode.Upgrade, ct);
        var snapshot = command.Snapshot;
        if (vault is null || vault.ArchivedAt is not null || vault.ProtectedKdbxPassword != command.ExpectedProtectedPassword
            || !m_snapshots.IsCurrent(vault.Id, snapshot.StateVersion))
        {
            return;
        }
        switch (snapshot.State)
        {
            case VaultRuntimeState.Fresh:
                var changed = vault.LastSnapshotSha256 is null || snapshot.Sha256 is null || !vault.LastSnapshotSha256.AsSpan().SequenceEqual(snapshot.Sha256);
                vault.State = VaultStates.Unlocked;
                vault.LastFailure = null;
                vault.LastRefreshAt = snapshot.CheckedAt;
                // The registry was synced together with LastSnapshotSha256, so the same bytes need no sync.
                if (changed)
                {
                    vault.LastSnapshotSha256 = snapshot.Sha256;
                    await VaultRegistry.SyncAsync(m_session, vault.Id, snapshot.Entries, snapshot.CheckedAt, ct);
                }
                break;
            case VaultRuntimeState.Unavailable:
                vault.LastFailure = snapshot.Failure;
                break;
            case VaultRuntimeState.Error:
                vault.State = VaultStates.Error;
                vault.LastFailure = snapshot.Failure;
                break;
            default:
                vault.State = VaultStates.Locked;
                vault.LastFailure = snapshot.Failure;
                break;
        }
    }
}
