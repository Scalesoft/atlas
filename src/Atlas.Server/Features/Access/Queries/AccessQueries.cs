namespace Atlas.Server.Features.Access.Queries;

/// <summary>The enabled access kinds (GET /api/access/kinds).</summary>
public sealed record GetAccessKinds;

/// <summary>The caller's own card (GET /api/access/me).</summary>
public sealed record GetMyAccess;

/// <summary>A person's or group's card (GET /api/access/principals/{kind}/{id}?spaceId=).</summary>
public sealed record GetPrincipalAccess(string Kind, Guid Id, Guid? SpaceId);

/// <summary>Who reaches one resource (GET /api/access/resources/{kind}/{id}?view=effective|direct&amp;q=), q a name or username.</summary>
public sealed record GetResourceAccess(string Kind, string Id, string? View, string? Query = null);

/// <summary>Resources of a kind the caller may grant (GET /api/access/resources?kind=&amp;q=).</summary>
public sealed record SearchAccessResources(string? Kind, string? Query);

/// <summary>Active users or groups matching a query, for someone who manages access (GET /api/access/candidates?kind=&amp;q=); with
/// <paramref name="ExceptMe"/> without the caller, for a share with others (a personal item's owner never shares with themself).</summary>
public sealed record GetAccessCandidates(string? Kind, string? Query, bool ExceptMe = false);

/// <summary>What revoke-all would close (GET /api/access/principals/user/{id}/revoke-all).</summary>
public sealed record GetRevokeAllImpact(Guid UserId);
