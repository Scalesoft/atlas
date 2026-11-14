using Atlas.YouTrack;

namespace Atlas.Server.Features.YouTrack.Domain;

/// <summary>One read of the effective integration configuration. Revision keys the read cache and detects a concurrent change while an upstream call was in flight. Its options hold the plain token: it stays inside the code that unprotected it and never travels in a message result (<see cref="YouTrackProtectedConfiguration"/>).</summary>
public sealed record YouTrackConfigurationSnapshot(string Revision, YouTrackOptions Options);
