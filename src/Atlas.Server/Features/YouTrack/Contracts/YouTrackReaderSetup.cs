namespace Atlas.Server.Features.YouTrack.Contracts;

/// <summary>What a reader learns of the integration: the spaces they read in which it is set up, and the YouTrack address its issue URLs
/// start with (null when there is no such space), so an editor recognises a pasted issue URL.</summary>
public sealed record YouTrackReaderSetup(IReadOnlyList<Guid> SpaceIds, string? BaseUrl);
