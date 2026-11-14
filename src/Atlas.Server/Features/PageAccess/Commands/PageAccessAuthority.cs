namespace Atlas.Server.Features.PageAccess.Commands;

/// <summary>Bounded delegation of page access management (WP3 S5), as SetSpaceGrantHandler bounds a temporary space Admin: the
/// grants that give the actor ManageAccess on the page decide how long it may grant for.</summary>
internal static class PageAccessAuthority
{
    /// <summary>Whether the actor may manage access to the page now, and until when: null for WikiAdmin or when one of the
    /// grants giving it ManageAccess (a space Admin grant or a page DocumentManager grant) has no expiry.</summary>
    public static async Task<(bool Allowed, DateTime? Until)> ReadAsync(ResourceGrantSnapshots grants, UserAccount actor, Page page,
        CancellationToken ct)
    {
        var snapshot = await grants.ForPageAsync(actor, page, ct);
        return snapshot.Authority(page, AccessOperation.ManageAccess, DateTimeOffset.UtcNow);
    }

    /// <summary>403 unless the actor manages the page now; a grant (role not null) may not outlast the actor's own authority.</summary>
    public static async Task RequireAsync(ResourceGrantSnapshots grants, UserAccount actor, Page page, int? role, DateTime? expiresAt,
        CancellationToken ct)
    {
        var (allowed, until) = await ReadAsync(grants, actor, page, ct);
        if (!allowed || (role is not null && until is { } end && (expiresAt is null || expiresAt > end)))
        {
            throw new ApiException(403, "Přístup nelze udělit na dobu delší, než je platnost vašeho oprávnění ke správě stránky.");
        }
    }
}
