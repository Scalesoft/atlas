namespace Atlas.Server.Features.Spaces.Commands;

public sealed record SetSpacePin(Guid SpaceId, bool Pinned);
