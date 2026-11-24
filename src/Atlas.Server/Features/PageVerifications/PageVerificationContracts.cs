namespace Atlas.Server.Features.PageVerifications;

/// <summary>
/// The page's verification as a reader sees it (plan §7): the bundle of the page view, <c>GET /api/v1/pages/{id}</c>, atlas_get_page.
/// The first seven are the plan's contract; the rest is what the chip's popover and the page panel show.
/// </summary>
/// <param name="State">off, unverified, verified, expiring or expired (<see cref="Domain.PageVerificationStates"/>).</param>
/// <param name="StewardId">The steward's account (garant); null without one or when the account is deactivated („bez garanta“).</param>
/// <param name="ChangedSinceVerification">„Změněno po ověření“: the published version is newer than the verified one.</param>
/// <param name="VerifiedViaAi">„přes AI“: verified over MCP, the personal API or the assistant, by the account named.</param>
/// <param name="IntervalMonths">The page's own interval; null inherits <paramref name="SpaceIntervalMonths"/>, 0 = „Neověřovat“.</param>
/// <param name="SpaceIntervalMonths">The space's interval; null = verification is off in the space.</param>
/// <param name="PublishedVersion">The version readers read; null for a page never published (it cannot be verified).</param>
public sealed record PageVerificationView(string State, Guid? StewardId, string? StewardName, DateTime? VerifiedAt, DateTime? VerifiedUntil,
    int? VerifiedVersion, bool ChangedSinceVerification, Guid? VerifiedById, string? VerifiedByName, bool VerifiedViaAi, int? IntervalMonths,
    int? SpaceIntervalMonths, int? PublishedVersion);

/// <summary>
/// <c>PUT /api/v1/pages/{id}/verification</c>: every field is optional, at least one is required, all apply together.
/// </summary>
/// <param name="Verify">True verifies the published version now, until <paramref name="Until"/> or now + the interval.</param>
/// <param name="Until">„Ověřit do…“: only with <paramref name="Verify"/>, in the future.</param>
/// <param name="StewardId">The new steward: an active account that may read the page.</param>
/// <param name="StewardFromLastAuthor">The steward becomes the author of the last published version.</param>
/// <param name="IntervalMonths">The page's own interval: 0 („Neověřovat“), 3, 6 or 12.</param>
/// <param name="InheritInterval">True: the page follows the space's interval again.</param>
public sealed record SetPageVerificationRequest(bool? Verify = null, DateTime? Until = null, Guid? StewardId = null, bool? StewardFromLastAuthor = null,
    int? IntervalMonths = null, bool? InheritInterval = null);

/// <summary>
/// Bulk actions of Stav obsahu (plan §6): verify or set the steward of the listed pages of the space. Without
/// <paramref name="PageIds"/> only <paramref name="StewardFromLastAuthor"/> is allowed and applies to every page of the space that is
/// verified and has no steward („Nastavit garanta podle posledního autora“ after verification is switched on).
/// </summary>
public sealed record SetSpaceVerificationRequest(IReadOnlyList<Guid>? PageIds = null, bool? Verify = null, Guid? StewardId = null,
    bool? StewardFromLastAuthor = null);

/// <param name="Updated">Pages changed.</param>
/// <param name="Skipped">Pages left as they were: without the right to edit, not of the space, never published, or without a last author.</param>
public sealed record SpaceVerificationResult(int Updated, int Skipped);

/// <summary>„Nahlásit neaktuální“: an optional note of the reader (at most 500 characters) for the steward.</summary>
public sealed record ReportPageOutdatedRequest(string? Note = null);

/// <param name="Notified">How many people were told (the steward, else up to three editors who last published it).</param>
public sealed record PageOutdatedReport(int Notified);

/// <summary>One page of the space's readership (plan §5.2): counts only, never who read.</summary>
public sealed record SpaceReadershipItem(Guid PageId, string Title, long Views, long Readers, DateTime? LastViewedAt, DateTime? LastPublishedAt);

/// <param name="NextCursor">For the next page of the list; null at its end.</param>
public sealed record SpaceReadership(IReadOnlyList<SpaceReadershipItem> Items, string? NextCursor);

/// <summary>The tiles of Stav obsahu: pages of the space with verification on, by state.</summary>
/// <param name="ExpiredOrUnverified">„Vypršelo / neověřeno“.</param>
/// <param name="WithoutSteward">„Bez garanta“ (none, or a deactivated account), whatever the state.</param>
public sealed record ContentHealthSummary(int Verified, int Expiring, int ExpiredOrUnverified, int WithoutSteward);

/// <summary>A row of Stav obsahu.</summary>
/// <param name="Path">The page's path as atlas_search shows it („Prostor › Rodič › Stránka“), only titles the reader may see.</param>
/// <param name="Readers">Distinct readers in the period; <paramref name="Views"/> the views.</param>
public sealed record ContentHealthItem(Guid PageId, string Title, string Path, string State, Guid? StewardId, string? StewardName,
    DateTime? VerifiedUntil, bool ChangedSinceVerification, long Readers, long Views, DateTime? LastViewedAt, DateTime? LastPublishedAt,
    int PublishedVersion, int? VerifiedVersion);

public sealed record ContentHealthList(ContentHealthSummary Summary, IReadOnlyList<ContentHealthItem> Items, int Total, string? NextCursor);
