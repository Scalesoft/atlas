namespace Atlas.Server.Features.Auth.Contracts;

public sealed record LinkedIdentitiesDto(IReadOnlyList<LinkedIdentityDto> Items);
