namespace Atlas.Server.Features.PageAccess;

/// <summary>One reason a person or group reaches a page. Kind: global (WikiAdmin), spaceAdmin (a space Admin grant reaches every
/// page, D4), space (a space default Reader/Writer grant, only without own access), page (a direct ResourceGrant on this page).
/// GroupId and Name name the group a grant reaches the person through; Name is empty for a direct grant. Version is the CAS
/// token of a page grant (its ActiveFrom) for changing it. Effective is false for a current page grant outside its validity.</summary>
public sealed record AccessSource(string Kind, string Name, string Role, Guid? GroupId = null, Guid? GrantId = null,
    DateTime? StartAt = null, DateTime? ExpiresAt = null, string? Version = null, bool Effective = true);

/// <summary>Role is the effective document role on the page (the highest effective source). LastViewedAt is the last authorized
/// open (PageAccessEvent page.opened); LastEditedAt the last saved revision or unsaved collaboration update of that person.</summary>
public sealed record AccessPerson(Guid UserId, string DisplayName, string Username, string? Role,
    bool IsWikiAdmin, bool CanReadPublished, bool CanReadDraft, bool CanEdit, IReadOnlyList<AccessSource> Sources,
    DateTime? LastViewedAt = null, DateTime? LastEditedAt = null);

/// <summary>A group with a grant that applies to the page; MemberCount counts its current active members.</summary>
public sealed record AccessGroup(Guid GroupId, string Name, int MemberCount, string? Role, IReadOnlyList<AccessSource> Sources);

/// <summary>Distinct active people per effective role and the impact of changing the access mode. Hidden: space members whose
/// default access the own access hides (they gain the page when it returns to the space defaults). LoseOnReset and
/// DowngradeOnReset: people who would lose the page or a part of their role when it returns to the space defaults (its page
/// grants are closed then); Losing names the first of them. DefaultGrants: space default Reader/Writer assignments that turning
/// own access on copies into page grants (D6).</summary>
public sealed record PageAccessCounts(int Manage, int Edit, int Read, int Hidden, int LoseOnReset, int DowngradeOnReset,
    int DefaultGrants, IReadOnlyList<string> Losing);

/// <summary>Anonymous links of the page that open now: not revoked, not expired, under the enabled sharing policy.</summary>
public sealed record ShareLinkSummary(int Active, DateTime? NextExpiry);

/// <summary>MyAccess is the caller's own access row, not the page access mode. Items and everything after HasMore are management
/// detail, present only when the caller may manage access to the page (CanManage). Mode: inherit (space defaults) or custom (own
/// access); ModeVersion is the CAS token of PUT access/mode.</summary>
public sealed record PageAccessState(Guid SpaceId, string SpaceName, bool IsPublished, bool CanManage,
    AccessPerson MyAccess, IReadOnlyList<AccessPerson> Items, bool HasMore,
    string PageTitle = "", string? Mode = null, string? ModeVersion = null, PageAccessCounts? Counts = null,
    IReadOnlyList<AccessGroup>? Groups = null, ShareLinkSummary? ShareLinks = null);

public sealed record PageAccessEvent(string Id, string Action, DateTime OccurredAt, Guid? ActorId,
    string? ActorName, Guid? ShareLinkId, string? ShareLinkName, int RevisionVersion)
{
    /// <summary>How a page.opened came in: web, api, mcp or system; null for other actions and openings from before Atlas 2.0.</summary>
    public string? Channel { get; init; }
    /// <summary>The token of an api or mcp opening, for its owner and wiki administrators only.</summary>
    public ApiTokenRef? ApiToken { get; init; }
}
public sealed record PageAccessHistory(IReadOnlyList<PageAccessEvent> Items, string? NextBeforeId)
{
    /// <summary>Who marked the page as a Projector catalog of its space or unmarked it (review A2-4), newest first, at most 50: only on
    /// the first page of the history and only when there is one.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PageCatalogMark>? CatalogMarks { get; init; }
}

/// <summary>One mark or unmark of a Projector catalog page; <see cref="Channel"/> web, api or mcp.</summary>
public sealed record PageCatalogMark(string Id, bool IsCatalog, DateTime OccurredAt, Guid ActorId, string? ActorName, string Channel)
{
    /// <summary>The token of an api or mcp change, for its owner and wiki administrators only.</summary>
    public ApiTokenRef? ApiToken { get; init; }
}

public sealed record UpdatePageAccessMode(bool OwnAccess, string? ExpectedVersion);
public sealed record PageAccessModeResult(string Mode, string Version);

/// <summary>Adds, changes (Role) or removes (Role null) the direct page grant of exactly one user or group.</summary>
public sealed record UpdatePageGrant(Guid? UserId, Guid? GroupId, string? Role, string? ExpectedVersion,
    DateTimeOffset? StartAt = null, DateTimeOffset? ExpiresAt = null);

/// <summary>"Zobrazit jako": the page decision for another account through the same policy. Denial explains a refusal:
/// inactive, privateDraft (an unpublished page of another owner), ownAccess (space default access hidden by own access),
/// none (no grant reaches the account).</summary>
public sealed record PageAccessExplanation(Guid UserId, string DisplayName, bool CanView, bool CanEdit, string? Role,
    IReadOnlyList<AccessSource> Sources, string? Denial);

/// <summary>A user or group a page manager may grant access to or view the page as. Detail: the username or the member count.</summary>
public sealed record AccessCandidate(Guid Id, string Kind, string Name, string? Detail);
