namespace Atlas.Server.Features.YouTrack.Commands;

/// <summary>Short names YouTrack just answered (<see cref="YouTrackReadRunner"/>: „Test spojení“, a space's projects), by stable
/// project ID; null drops a project YouTrack no longer has. Stored on the configuration of <c>Revision</c> only while it is current,
/// in place: derived data, not a settings change, so the revision and the read caches stay.</summary>
public sealed record StoreYouTrackProjectKeys(string Revision, IReadOnlyDictionary<string, string?> Keys);
