namespace Atlas.Server.Features.Collaboration.Domain;

/// <summary>Live head of one generation without the snapshot bytes or projections. <paramref name="Behind"/>: its base is not the page's current
/// revision, whose live write has not appended its update yet (LivePageWrites).</summary>
public sealed record CollaborationHead(Guid PageId, Guid Generation, long Sequence, long SnapshotSequence, int PendingCount, long PendingBytes, long LastSavedSequence,
    bool Behind);
