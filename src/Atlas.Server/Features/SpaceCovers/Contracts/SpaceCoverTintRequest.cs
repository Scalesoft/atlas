namespace Atlas.Server.Features.SpaceCovers.Contracts;

/// <param name="Tinted">Required; nullable only so that a body without it is refused instead of read as false.</param>
public sealed record SpaceCoverTintRequest(bool? Tinted);
