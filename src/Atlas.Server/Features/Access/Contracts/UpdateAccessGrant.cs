namespace Atlas.Server.Features.Access.Contracts;

/// <summary>PUT /api/access/resources/{kind}/{id}/grants: the grant of one user or group (SubjectKind user or group) at a level of
/// the kind. ExpectedVersion is the current grant's version, null when there is none.</summary>
public sealed record UpdateAccessGrant(string? SubjectKind, Guid? SubjectId, string? Level, DateTimeOffset? StartAt = null,
    DateTimeOffset? ExpiresAt = null, string? ExpectedVersion = null);
