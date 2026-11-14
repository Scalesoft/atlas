namespace Atlas.Server.Features.Administration.Contracts;

public sealed record GrantSummary(Guid Id, Guid? UserId, Guid? GroupId, string Name, string Role, string Version,
    DateTime? StartAt = null, DateTime? ExpiresAt = null);
