namespace Atlas.Server.Features.Auth.Contracts;

public sealed record LinkedIdentityDto(string ProviderId, string DisplayName, DateTime LinkedAt);
