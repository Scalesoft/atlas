namespace Atlas.Server.Platform.Objects;

/// <summary>
/// The trash of a type whose objects live in its module's tables (unified trash spec §2, rule 5): a page, a board of a space. The
/// object path's restore and permanent delete (ObjectTrashHandler) hand the whole command to it, so one route serves every type; it
/// takes its own locks, decides who may (anyone else gets 404, as a missing object) and writes its audit (ObjectPurge.AuditAsync).
/// A type the platform stores (IObjectChangeHandler) needs none: the object path restores and purges its envelope itself.
/// </summary>
public interface IObjectTrash
{
    /// <summary>The ContentResource.TypeId it answers for.</summary>
    string TypeId { get; }

    /// <summary>Back to where it was; the version it has then.</summary>
    Task<long> RestoreAsync(Guid objectId, CancellationToken ct);

    /// <summary>Permanently, only from the trash.</summary>
    Task PurgeAsync(Guid objectId, CancellationToken ct);
}
