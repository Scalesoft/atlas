namespace Atlas.Server.Platform.Identity;

/// <summary>Explicitly supported types; adding a type never extends an existing role. Vaults and secrets have their own
/// level evaluator (Features/Vaults/VaultGrantSnapshot), so documents are the only kind this policy decides.</summary>
public enum AccessResourceKind { Document = 1 }
public enum AccessOperation { Discover = 1, ReadContent = 2, EditContent = 3, ManageAccess = 4, Share = 5 }
public enum AccessRole { DocumentReader = 1, DocumentWriter = 2, DocumentManager = 3 }
public enum AccessSubjectKind { User = 1, Group = 2, ShareLink = 3 }
public enum AccessScopeKind { SpaceDefaults = 1, Resource = 2 }
public enum AccessContextKind { Internal = 1, Shared = 2 }

/// <summary>Trusted server snapshot, not a DTO accepted from clients. Shared contexts ignore internal identity.</summary>
public sealed record ResourceAccessPrincipal(
    AccessContextKind Context, Guid SubjectId, bool IsActive,
    bool IsWikiAdmin, IReadOnlySet<Guid> GroupIds, DateTimeOffset? HubWikiAdminUntil = null)
{
    public bool IsWikiAdminAt(DateTimeOffset now) => IsWikiAdmin || HubWikiAdminUntil is { } until && now < until;
}

/// <summary>Ownership and inheritance flags must be loaded from the authoritative resource, never from the request.</summary>
public sealed record ResourceAccessTarget(Guid SpaceId, AccessResourceKind Kind, Guid Id, bool OwnAccess = false, bool IsTemplate = false);

/// <summary>A current assignment. StartsAt/ExpiresAt describe business validity, not temporal row history.</summary>
public sealed record ResourceAccessGrant(
    Guid Id, AccessSubjectKind SubjectKind, Guid SubjectId,
    Guid SpaceId, AccessScopeKind Scope, AccessResourceKind ResourceKind, Guid? ResourceId,
    AccessRole Role, DateTimeOffset StartsAt, DateTimeOffset? ExpiresAt = null, bool Revoked = false);

/// <summary>A document decision with the grants that explain it; LegacyAdministrator when WikiAdmin decided it.</summary>
public sealed record ResourceAccessDecision(bool Allowed, IReadOnlyList<Guid> GrantIds, bool LegacyAdministrator = false);
