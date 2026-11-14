namespace Atlas.Server.Features.YouTrack.Queries;

/// <summary>Step 2 of a YouTrack read (<see cref="YouTrackReadRunner"/>), one short transaction after the call: the actor is authorized
/// again (401, 404 or 403 when the right was lost meanwhile) and the configuration must still have the revision the call used (409
/// otherwise). Only then is the result cached and released.</summary>
public sealed record EnsureYouTrackUnchanged(Guid? SpaceId, string Revision);
