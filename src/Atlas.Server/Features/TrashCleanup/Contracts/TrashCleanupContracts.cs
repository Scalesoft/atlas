namespace Atlas.Server.Features.TrashCleanup.Contracts;

/// <summary>The wiki's rule. Version is null until it is first changed (each space decides, manually, after 60 days).</summary>
public sealed record WikiTrashCleanupView(string Scope, bool Automatic, int Days, string? Version);

public sealed record SaveWikiTrashCleanupRequest(string? Scope, bool? Automatic, int? Days, string? ExpectedVersion);

/// <summary>A space's cleanup: its own rule (Automatic, Days, Version) and the one in effect, which is the wiki's when
/// <see cref="DecidedByWiki"/>; what lies in its trash and what nobody uses, and how much of it is older than the effective days.</summary>
public sealed record SpaceTrashCleanupView(bool Automatic, int Days, string? Version, bool DecidedByWiki, bool EffectiveAutomatic, int EffectiveDays,
    TrashCleanupCounts Counts);

/// <param name="Trashed">Items in the space's trash.</param>
/// <param name="TrashedDue">Of them, those in the trash longer than the effective days: a cleanup deletes them permanently.</param>
/// <param name="UnusedDue">Objects nobody uses, unchanged for the effective days: a cleanup moves them to the trash.</param>
public sealed record TrashCleanupCounts(int Trashed, int TrashedDue, int UnusedDue);

public sealed record SaveSpaceTrashCleanupRequest(bool? Automatic, int? Days, string? ExpectedVersion);

/// <summary>What one cleanup did: items deleted permanently, unused objects moved to the trash, and those it had to leave (a right
/// or a lock of someone else's live edit refused).</summary>
public sealed record TrashCleanupResult(int Purged, int Trashed, int Skipped);
