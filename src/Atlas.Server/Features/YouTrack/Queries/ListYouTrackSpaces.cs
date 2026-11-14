namespace Atlas.Server.Features.YouTrack.Queries;

/// <summary>The spaces the actor reads in which the integration is set up (enabled, a connection, an allowed project): the omnisearch
/// asks YouTrack only for these (UX SE3-2), not once per space on every query. The YouTrack address comes along for the editor's
/// recognition of a pasted issue URL.</summary>
public sealed record ListYouTrackSpaces();
