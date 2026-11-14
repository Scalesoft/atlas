namespace Atlas.Server.Platform.Identity;

/// <summary>Authorization at a content transaction boundary: lock the actor, then resolve the effective space role for that locked account. Same lock order as page and administration writes; RequireSpaceAsync requires a prior LockActorAsync in the same scope. The account LockActorAsync returns is the only authorization-grade actor: handlers decide on it, never on ICurrentActor, whose instance reflects the request claims rather than the row read under the lock.</summary>
/// <remarks>Scoped to one transaction: a Wolverine message has its own DI scope, session and transaction. Row locks end with the
/// transaction, so the locked spaces and the per-space grant cache are forgotten when LockActorAsync runs in a new transaction, and
/// a page decision outside the transaction that took the locks throws InvalidOperationException.</remarks>
public interface IContentAccess
{
    /// <summary>Takes the shared content lock, FOR SHARE on the space (when given) and on the account, and re-reads the account. Every space locked in this transaction is remembered for the page decisions.</summary>
    Task<UserAccount> LockActorAsync(Guid? spaceId, CancellationToken ct);

    /// <summary>LockActorAsync for several spaces (a cross-space move): FOR SHARE on each space in identifier order, the account once.</summary>
    Task<UserAccount> LockActorInSpacesAsync(IReadOnlyCollection<Guid> spaceIds, CancellationToken ct);

    Task<SpaceRole> RequireSpaceAsync(Guid spaceId, SpaceRole required, CancellationToken ct);

    /// <summary>Item access for one page without throwing on denial: ResourceGrantSnapshot.Authorize over the locked account's grants,
    /// that is ResourceAccessPolicy plus the private draft rule. Paths that answer a denial their own way (a 400 for a target parent,
    /// 404 for history) branch on the outcome; the others call RequirePageAsync.</summary>
    /// <remarks>
    /// <para>Lock contract: call LockActorAsync(page.SpaceId) first, then read or refresh the page under that lock (a page loaded
    /// before the lock must be refreshed). Without a locked space this throws InvalidOperationException; a page whose SpaceId
    /// is none of the spaces locked in this transaction was moved concurrently and is refused with 409 space_changed, as page
    /// writes do. The FOR SHARE lock on the space keeps the grant snapshot stable, so writes of ResourceGrant rows and of
    /// Page.OwnAccess take LockSpaceForUpdate on the page's space and then the page lock. Page grants and OwnAccess are read
    /// afresh for every decision; the space defaults (the account's SpaceGrant rows) are cached per space and transaction, so a
    /// SpaceGrant or membership write must not be followed by a page decision of that space in the same transaction.</para>
    /// <para>Outcome: Missing when the page is invisible (another owner's unpublished draft, unless WikiAdmin) or the account may
    /// not Discover/ReadContent it; Forbidden when the page is readable but the requested operation is denied.</para>
    /// <para>Not decided here: trash checks (a trashed page is 404 on content paths yet listed in the trash) and the moved-page
    /// 409 of paths that resolve the space before locking, which PageGate makes for the paths that go through it and the
    /// others at their call sites; and path-specific rules kept at the call sites, such as AttachmentPageGuard (a draft's
    /// attachments need EditContent and ownership or WikiAdmin) and PageHistoryAccess (EditContent, a readable page still
    /// answers 404 as in 1.0).</para>
    /// <para>Paths that authorize an account other than the request actor or hold no content locks (collaboration relay,
    /// export build and revalidation, share link issuer) call ResourceGrantSnapshot.Authorize themselves.</para>
    /// </remarks>
    Task<PageAuthorization> AuthorizePageAsync(Page page, AccessOperation operation, CancellationToken ct);

    /// <summary>AuthorizePageAsync for several locked pages with one grant query per space, in the order given.</summary>
    Task<IReadOnlyList<PageAuthorization>> AuthorizePagesAsync(IReadOnlyList<Page> pages, AccessOperation operation, CancellationToken ct);

    /// <summary>AuthorizePageAsync with the standard answer (PageAuthorization.Require): 404 when Missing, 403 when Forbidden.</summary>
    Task<PageAuthorization> RequirePageAsync(Page page, AccessOperation operation, CancellationToken ct);
}
