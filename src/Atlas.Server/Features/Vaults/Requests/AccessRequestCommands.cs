namespace Atlas.Server.Features.Vaults.Requests;

/// <summary>Asks for Čte on a whole vault; a pending request of the caller for the same target is returned as is.</summary>
public sealed record RequestVaultAccess(Guid VaultId, CreateAccessRequest Request);

/// <summary>Asks for Čte on one secret; a pending request of the caller for the same target is returned as is.</summary>
public sealed record RequestSecretAccess(Guid SecretId, CreateAccessRequest Request);

/// <summary>A manager of the target approves: the grant is written through the V3 grant path (VaultGrantHandler).</summary>
public sealed record ApproveAccessRequest(Guid RequestId, DateTimeOffset? ExpiresAt);

public sealed record RejectAccessRequest(Guid RequestId, string? Note);

/// <summary>The requester withdraws a pending request.</summary>
public sealed record CancelAccessRequest(Guid RequestId);
