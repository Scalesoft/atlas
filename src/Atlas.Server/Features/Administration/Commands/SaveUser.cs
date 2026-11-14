using Atlas.Server.Features.Administration.Contracts;

namespace Atlas.Server.Features.Administration.Commands;

public sealed record SaveUser(Guid UserId, UpdateUser Request);
