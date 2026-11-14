namespace Atlas.Server.Features.Access.Domain;

/// <summary>A grant change for <see cref="IAccessGrantWriter"/>: exactly one of UserId and GroupId, a level of the kind (null
/// revokes), business validity and the CAS version of the current grant.</summary>
public sealed record AccessGrantChange(Guid? UserId, Guid? GroupId, string? Level, DateTimeOffset? StartAt, DateTimeOffset? ExpiresAt,
    string? ExpectedVersion);
