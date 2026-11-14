using Atlas.Server.Features.Spaces.Contracts;

namespace Atlas.Server.Features.Spaces.Commands;

public sealed record TrashSpace(Guid SpaceId, TrashSpaceRequest Request);
public sealed record RestoreSpace(Guid SpaceId);
public sealed record PurgeSpace(Guid SpaceId, PurgeSpaceRequest Request);
