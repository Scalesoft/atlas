namespace Atlas.Server.Features.YouTrack.Queries;

/// <summary>Step 1 of a YouTrack read (<see cref="YouTrackReadRunner"/>): the actor is authorized (Reader of the space, or WikiAdmin for
/// the connection test, which has no space) and the effective configuration is read. The YouTrack call runs after this transaction has
/// ended. The answer keeps the token data-protected, because any Reader may send this message; the runner unprotects it right after
/// the answer arrives.</summary>
public sealed record ReadYouTrackConfiguration(Guid? SpaceId);
