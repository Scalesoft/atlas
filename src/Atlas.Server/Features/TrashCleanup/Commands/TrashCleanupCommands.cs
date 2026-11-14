using Atlas.Server.Features.TrashCleanup.Contracts;

namespace Atlas.Server.Features.TrashCleanup.Commands;

public sealed record GetWikiTrashCleanup;
public sealed record SaveWikiTrashCleanup(SaveWikiTrashCleanupRequest Request);
public sealed record GetSpaceTrashCleanup(Guid SpaceId);
public sealed record SaveSpaceTrashCleanup(Guid SpaceId, SaveSpaceTrashCleanupRequest Request);
