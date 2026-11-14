namespace Atlas.Server.Features.AccessAudit.Queries;

/// <summary>A page of the access audit, newest first. VaultId narrows it to the grant events of one vault and its secrets; UserId to the
/// events a person made or that are about them (actor or subject), GroupId to those of a group and SpaceId to those of a space
/// (its grants, page grants, vaults and secrets). Filters combine.</summary>
public sealed record GetAccessAudit(string? BeforeId, int Limit, Guid? VaultId = null, Guid? UserId = null, Guid? GroupId = null,
    Guid? SpaceId = null);
