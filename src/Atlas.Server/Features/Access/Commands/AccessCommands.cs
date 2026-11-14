using Atlas.Server.Features.Access.Contracts;

namespace Atlas.Server.Features.Access.Commands;

/// <summary>Sets or changes a grant through the kind's writer (PUT /api/access/resources/{kind}/{id}/grants).</summary>
public sealed record SetAccessGrant(string Kind, Guid ResourceId, UpdateAccessGrant Request);

/// <summary>Closes a grant through the kind's writer (DELETE /api/access/resources/{kind}/{id}/grants/{subjectKind}/{subjectId}).</summary>
public sealed record RevokeAccessGrant(string Kind, Guid ResourceId, string SubjectKind, Guid SubjectId, string? Version);

/// <summary>Closes every direct grant and group membership of an account and revokes its personal API tokens
/// (POST /api/access/principals/user/{id}/revoke-all).</summary>
public sealed record RevokeAllAccess(Guid UserId);
