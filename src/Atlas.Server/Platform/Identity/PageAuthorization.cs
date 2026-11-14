namespace Atlas.Server.Platform.Identity;

/// <summary>How a page decision ends: Missing when the account may not even learn that the page exists (404), Forbidden when it
/// may read the page but not perform the operation (403), Allowed otherwise.</summary>
public enum PageAccessOutcome
{
    Missing,
    Forbidden,
    Allowed
}

/// <summary>One page decision: the outcome for the requested operation, the policy decision with its explaining grants, and whether
/// the same account may edit the page's working content. Made by ResourceGrantSnapshot.Authorize, the only place that combines
/// ResourceAccessPolicy with the private draft rule (PageVisibility).</summary>
public sealed record PageAuthorization(PageAccessOutcome Outcome, AccessOperation Operation, ResourceAccessDecision Decision, bool CanEdit)
{
    public bool Allowed => Outcome == PageAccessOutcome.Allowed;

    /// <summary>An invisible page: another owner's unpublished draft, decided without loading any grant.</summary>
    public static PageAuthorization Missing(AccessOperation operation)
    {
        return new PageAuthorization(PageAccessOutcome.Missing, operation, new ResourceAccessDecision(false, []), false);
    }

    /// <summary>The standard answer of point-of-use page paths: 404 for Missing, 403 for Forbidden, this decision when allowed.</summary>
    public PageAuthorization Require()
    {
        return Outcome switch
        {
            PageAccessOutcome.Allowed => this,
            PageAccessOutcome.Forbidden => throw new ApiException(403, Operation == AccessOperation.EditContent
                ? "K této operaci potřebujete oprávnění k úpravám stránky."
                : "K této operaci potřebujete oprávnění ke správě stránky."),
            _ => throw new ApiException(404, "Stránka nebyla nalezena.")
        };
    }
}
