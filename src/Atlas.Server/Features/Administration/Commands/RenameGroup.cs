using Atlas.Server.Features.Administration.Contracts;

namespace Atlas.Server.Features.Administration.Commands;

public sealed record RenameGroup(Guid GroupId, UpdateGroup Request);
