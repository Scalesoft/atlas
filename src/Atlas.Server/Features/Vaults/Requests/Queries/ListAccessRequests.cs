namespace Atlas.Server.Features.Vaults.Requests.Queries;

/// <summary>GET /api/access-requests?state=pending|all&amp;vaultId=: requests the caller can approve or reject now (Spravuje on
/// the target), pending by default.</summary>
public sealed record ListAccessRequests(string? State, Guid? VaultId);

/// <summary>GET /api/access-requests/mine: the caller's own requests.</summary>
public sealed record ListMyAccessRequests;
