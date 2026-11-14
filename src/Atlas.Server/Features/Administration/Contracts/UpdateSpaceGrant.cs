namespace Atlas.Server.Features.Administration.Contracts;

public sealed record UpdateSpaceGrant(Guid? UserId, Guid? GroupId, string? Role, string? ExpectedVersion,
    DateTimeOffset? StartAt = null, DateTimeOffset? ExpiresAt = null);
