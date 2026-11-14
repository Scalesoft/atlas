namespace Atlas.Server.Features.Vaults.Requests;

/// <summary>POST /api/secrets/{id}/access-requests and /api/vaults/{id}/access-requests. Level: read (Čte), the only level that
/// can be requested. Reason: required, at most 500 characters; it is shown to the managers of the target.</summary>
public sealed record CreateAccessRequest(string? Level, string? Reason);

/// <summary>POST /api/access-requests/{id}/approve. ExpiresAt defaults to 30 days and never outlasts the approver's own
/// authority; an explicit later end is refused (403).</summary>
public sealed record ApproveAccessRequestBody(DateTimeOffset? ExpiresAt);

/// <summary>POST /api/access-requests/{id}/reject; Note is optional and shown to the requester.</summary>
public sealed record RejectAccessRequestBody(string? Note);

/// <summary>
/// One access request. SecretId/SecretTitle are null for a request on the whole vault. State: pending, approved, rejected or
/// cancelled. DecidedBy is the approver or rejecter, or the requester for a cancellation. GrantId and GrantExpiresAt describe the
/// grant an approval created (or the current one that already covered it). CanManage: the caller may approve or reject it now.
/// </summary>
public sealed record AccessRequestView(Guid Id, Guid SpaceId, Guid VaultId, string VaultName, Guid? SecretId, string? SecretTitle,
    string Level, string Reason, string State, Guid RequesterId, string RequesterName, DateTime CreatedAt,
    Guid? DecidedBy, string? DecidedByName, DateTime? DecidedAt, string? Note, Guid? GrantId, DateTime? GrantExpiresAt, bool CanManage);

/// <summary>Requests newest first; HasMore when the list was cut at its limit.</summary>
public sealed record AccessRequestList(IReadOnlyList<AccessRequestView> Items, bool HasMore);
