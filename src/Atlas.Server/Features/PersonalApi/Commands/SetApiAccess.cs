using Atlas.Server.Features.PersonalApi.Contracts;

namespace Atlas.Server.Features.PersonalApi.Commands;

public sealed record SetApiAccess(Guid UserId, ChangeApiPermission Request);
