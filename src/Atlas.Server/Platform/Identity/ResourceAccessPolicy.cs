namespace Atlas.Server.Platform.Identity;

/// <summary>
/// Pure policy evaluation over authoritative snapshots. Point-of-use page paths decide through it (IContentAccess.RequirePageAsync,
/// ResourceGrantSnapshots); lists and search decide through the EffectivePageAccess view (PageListing), which must agree with it.
/// Every page is decided on its own: space defaults apply unless the page has own access, a direct page grant
/// applies to exactly that page, and a space-default DocumentManager (the space Admin) reaches every page.
/// Shared-link validity, pinned revision/attachment scope and issuer rights must be validated by the
/// sharing service before constructing the snapshot. This is not an authentication or release gate. Documents only: vaults and
/// secrets have one access level per grant, decided by Features/Vaults/VaultGrantSnapshot.
/// </summary>
public static class ResourceAccessPolicy
{
    /// <summary>Compatibility bridge for 1.0 content callers. Numeric ordering must not authorize future roles.</summary>
    public static bool AllowsLegacyContent(bool wikiAdmin, SpaceRole? grant, SpaceRole required)
    {
        var operation = required switch
        {
            SpaceRole.Reader => AccessOperation.ReadContent,
            SpaceRole.Writer => AccessOperation.EditContent,
            SpaceRole.Admin => AccessOperation.ManageAccess,
            _ => (AccessOperation)0
        };
        var role = wikiAdmin ? AccessRole.DocumentManager : grant switch
        {
            SpaceRole.Reader => AccessRole.DocumentReader,
            SpaceRole.Writer => AccessRole.DocumentWriter,
            SpaceRole.Admin => AccessRole.DocumentManager,
            _ => (AccessRole)0
        };
        return DocumentRoleAllows(role, operation);
    }

    public static ResourceAccessDecision Evaluate(
        ResourceAccessPrincipal principal, ResourceAccessTarget target, AccessOperation operation,
        IEnumerable<ResourceAccessGrant> grants, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(grants);
        if (!principal.IsActive || principal.SubjectId == Guid.Empty || !ValidTarget(target))
            return Denied();

        if (principal.Context == AccessContextKind.Shared)
            return EvaluateShare(principal.SubjectId, target, operation, grants, now);
        if (principal.Context != AccessContextKind.Internal)
            return Denied();

        if (principal.IsWikiAdminAt(now) && DocumentRoleAllows(AccessRole.DocumentManager, operation))
            return new(true, [], LegacyAdministrator: true);

        var templateControl = target.IsTemplate && operation is AccessOperation.EditContent or AccessOperation.ManageAccess or AccessOperation.Share;
        var matching = grants.Where(grant =>
                IsCurrent(grant, now) && InternalSubjectMatches(principal, grant)
                && ScopeMatches(target, grant) && DocumentRoleAllows(grant.Role, operation)
                && (!templateControl || grant.Scope == AccessScopeKind.SpaceDefaults && grant.Role == AccessRole.DocumentManager))
            .Select(grant => grant.Id).Distinct().ToArray();
        return Decision(matching);
    }

    private static ResourceAccessDecision EvaluateShare(Guid linkId, ResourceAccessTarget target,
        AccessOperation operation, IEnumerable<ResourceAccessGrant> grants, DateTimeOffset now)
    {
        if (operation is not (AccessOperation.Discover or AccessOperation.ReadContent))
            return Denied();

        var matching = grants.Where(grant => IsCurrent(grant, now)
                && grant.SubjectKind == AccessSubjectKind.ShareLink && grant.SubjectId == linkId
                && grant.Role == AccessRole.DocumentReader
                && grant.Scope == AccessScopeKind.Resource
                && grant.SpaceId == target.SpaceId
                && grant.ResourceKind == AccessResourceKind.Document && grant.ResourceId == target.Id)
            .Select(grant => grant.Id).Distinct().ToArray();
        return Decision(matching);
    }

    private static bool ValidTarget(ResourceAccessTarget target) =>
        target.Id != Guid.Empty && target.SpaceId != Guid.Empty && target.Kind == AccessResourceKind.Document;

    private static bool IsCurrent(ResourceAccessGrant grant, DateTimeOffset now) =>
        grant.Id != Guid.Empty && grant.SubjectId != Guid.Empty && !grant.Revoked
        && grant.StartsAt <= now && (grant.ExpiresAt is null || now < grant.ExpiresAt);

    private static bool InternalSubjectMatches(ResourceAccessPrincipal principal, ResourceAccessGrant grant) =>
        grant.SubjectKind switch
        {
            AccessSubjectKind.User => grant.SubjectId == principal.SubjectId,
            AccessSubjectKind.Group => principal.GroupIds.Contains(grant.SubjectId),
            _ => false
        };

    private static bool ScopeMatches(ResourceAccessTarget target, ResourceAccessGrant grant)
    {
        if (grant.SpaceId != target.SpaceId || grant.ResourceKind != target.Kind) return false;
        if (grant.Scope == AccessScopeKind.Resource) return grant.ResourceId == target.Id;
        if (grant.Scope != AccessScopeKind.SpaceDefaults || grant.ResourceId is not null) return false;
        // The space Admin keeps document management on own-access pages; private content belongs in another space.
        return !target.OwnAccess || grant.Role == AccessRole.DocumentManager;
    }

    private static bool DocumentRoleAllows(AccessRole role, AccessOperation operation) =>
        role switch
        {
            AccessRole.DocumentReader => operation is AccessOperation.Discover or AccessOperation.ReadContent,
            AccessRole.DocumentWriter => operation is AccessOperation.Discover or AccessOperation.ReadContent or AccessOperation.EditContent,
            AccessRole.DocumentManager => operation is AccessOperation.Discover or AccessOperation.ReadContent
                or AccessOperation.EditContent or AccessOperation.ManageAccess or AccessOperation.Share,
            _ => false
        };

    private static ResourceAccessDecision Decision(Guid[] matching) =>
        matching.Length == 0 ? Denied() : new(true, Array.AsReadOnly(matching));

    private static ResourceAccessDecision Denied() => new(false, []);
}
