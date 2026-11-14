using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Features.Vaults.Runtime;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Commands;

/// <summary>
/// Synchronizes VaultSecret rows with a Fresh snapshot: a new entry gets a new stable secret id, a known entry
/// keeps its id and gets the current title, path and fields, a missing entry gets RemovedAt, and an entry that
/// comes back is restored under its old id.
/// </summary>
public static class VaultRegistry
{
    public static async Task SyncAsync(ISession session, Guid vaultId, IReadOnlyList<VaultSnapshotEntry> entries, DateTime now, CancellationToken ct)
    {
        var rows = (await session.QueryOver<VaultSecret>().Where(s => s.VaultId == vaultId).ListAsync(ct)).ToDictionary(s => s.EntryUuid);
        var present = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            if (!present.Add(entry.Uuid))
            {
                continue;
            }
            if (rows.TryGetValue(entry.Uuid, out var row))
            {
                if (row.Title != entry.Title)
                {
                    row.Title = entry.Title;
                }
                if (row.GroupPath != entry.GroupPath)
                {
                    row.GroupPath = entry.GroupPath;
                }
                if (row.AvailableFields != entry.Fields)
                {
                    row.AvailableFields = entry.Fields;
                }
                if (row.RemovedAt is not null)
                {
                    row.RemovedAt = null;
                }
                continue;
            }
            await session.SaveAsync(new VaultSecret
            {
                Id = Guid.NewGuid(),
                VaultId = vaultId,
                EntryUuid = entry.Uuid,
                Title = entry.Title,
                GroupPath = entry.GroupPath,
                AvailableFields = entry.Fields,
                FirstSeenAt = now,
            }, ct);
        }
        foreach (var row in rows.Values)
        {
            if (row.RemovedAt is null && !present.Contains(row.EntryUuid))
            {
                row.RemovedAt = now;
            }
        }
    }
}
