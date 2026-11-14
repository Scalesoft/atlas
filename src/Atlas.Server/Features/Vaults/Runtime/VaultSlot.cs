using Atlas.Server.Features.Vaults.Kdbx;
using Atlas.Server.Platform.Storage;

namespace Atlas.Server.Features.Vaults.Runtime;

/// <summary>Per-vault state of <see cref="VaultSnapshots"/>. Mutated only while <see cref="Gate"/> is held.</summary>
internal sealed class VaultSlot
{
    private long m_version;
    private volatile VaultSnapshot? m_current;

    /// <summary>Single flight per vault: one check or reload at a time.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public VaultSnapshot? Current => m_current;

    /// <summary>The last Fresh snapshot, kept for a file that comes back unchanged after being unavailable.</summary>
    public VaultSnapshot? LastGood { get; private set; }

    /// <summary>Metadata of the file the current Fresh snapshot was read from.</summary>
    public FileStorageMetadata? Metadata { get; set; }

    /// <summary>
    /// Publishes a new state. The version grows when the state or the content changes; a repeated identical
    /// outcome only moves <see cref="VaultSnapshot.CheckedAt"/>.
    /// </summary>
    public VaultSnapshot Publish(Guid vaultId, VaultRuntimeState state, IReadOnlyList<VaultSnapshotEntry>? entries, byte[]? sha256,
        KdbxFailure? failure, string? message)
    {
        var fresh = state == VaultRuntimeState.Fresh;
        var previous = m_current;
        var unchanged = previous is not null && previous.State == state && previous.Failure == message
            && (previous.Sha256 ?? []).AsSpan().SequenceEqual(fresh ? sha256 ?? [] : []);
        var version = unchanged ? previous!.StateVersion : Interlocked.Increment(ref m_version);
        var snapshot = new VaultSnapshot(vaultId, state, version, fresh ? entries ?? [] : [], fresh ? sha256 : null,
            failure, message, DateTime.UtcNow);
        if (fresh)
        {
            LastGood = snapshot;
        }
        else
        {
            Metadata = null;
            if (state is not VaultRuntimeState.Unavailable)
            {
                LastGood = null;
            }
        }
        m_current = snapshot;
        return snapshot;
    }
}
