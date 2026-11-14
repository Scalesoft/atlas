namespace Atlas.Server.Features.YouTrack.Contracts;

/// <summary>The references a board paste (or any editor) resolves at once: issue IDs or issue URLs of the configured YouTrack, at most 100.</summary>
public sealed record YouTrackResolveRequest(string[]? References);
