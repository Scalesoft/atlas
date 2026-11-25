namespace Atlas.Server.Features.PageVerifications;

/// <summary>
/// The page's verification as a reader sees it (plan §7): the bundle of the page view, <c>GET /api/v1/pages/{id}</c>, atlas_get_page.
/// The first seven are the plan's contract; the rest is what the chip's popover and the page panel show.
/// </summary>
/// <param name="State">off, unverified, verified, expiring or expired (<see cref="Domain.PageVerificationStates"/>).</param>
/// <param name="StewardId">The steward's account (garant); null without one, or when the account is deactivated or cannot read the page („bez garanta“).</param>
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
/// <param name="ClearSteward">True: the page has no steward any more.</param>
/// <param name="StewardFromLastAuthor">The steward becomes the author of the last published version.</param>
/// <param name="IntervalMonths">The page's own interval: 0 („Neověřovat“), 3, 6 or 12.</param>
/// <param name="InheritInterval">True: the page follows the space's interval again.</param>
public sealed record SetPageVerificationRequest(bool? Verify = null, DateTime? Until = null, Guid? StewardId = null, bool? StewardFromLastAuthor = null,
    int? IntervalMonths = null, bool? InheritInterval = null, bool? ClearSteward = null);

/// <summary>
/// Bulk actions of Stav obsahu (plan §6) over pages of the space: <paramref name="Action"/> verify, steward (to <paramref name="StewardId"/>,
/// null clears it) or stewardFromLastAuthor (the author of the last published version). Without <paramref name="PageIds"/> only
/// stewardFromLastAuthor, for every verified page of the space without a steward (the first fill after verification is switched on).
/// </summary>
public sealed record SetSpaceVerificationRequest(string Action, IReadOnlyList<Guid>? PageIds = null, Guid? StewardId = null);

/// <param name="Reason">The refusal: its problem code where it has one (not_published, …), else the HTTP status (403, 404, 400).</param>
public sealed record SkippedPage(Guid PageId, string Reason);

/// <param name="Changed">Pages changed.</param>
/// <param name="Skipped">Pages left as they were, with the reason.</param>
/// <param name="More">Without pageIds: more pages without a steward than one run takes (500); run it again.</param>
public sealed record SpaceVerificationResult(int Changed, IReadOnlyList<SkippedPage> Skipped, bool More = false);

/// <summary>„Nahlásit neaktuální“: an optional note of the reader (at most 500 characters) for the steward.</summary>
public sealed record ReportPageOutdatedRequest(string? Note = null);

/// <param name="Notified">How many people were told (the steward, else up to three editors who last published it).</param>
public sealed record PageOutdatedReport(int Notified);

/// <summary>The tiles of Stav obsahu: pages of the space with verification on, by state.</summary>
/// <param name="WithoutSteward">„Bez garanta“ (none, or a deactivated account), whatever the state.</param>
public sealed record ContentHealthSummary(int Verified, int Expiring, int Expired, int Unverified, int WithoutSteward);

/// <summary>A row of Stav obsahu.</summary>
/// <param name="Path">The titles of its ancestors from the root, as far as the reader may read them (a hidden parent ends it).</param>
/// <param name="Readers">Distinct readers in the period; <paramref name="Views"/> the views.</param>
/// <param name="LastViewedAt">The day of the last view in the period (Prague), no time: counts only, never who.</param>
public sealed record ContentHealthItem(Guid PageId, string Title, IReadOnlyList<string> Path, string State, Guid? StewardId, string? StewardName,
    DateTime? VerifiedUntil, bool ChangedSinceVerification, long Readers, long Views, DateOnly? LastViewedAt, DateTime? LastPublishedAt,
    int PublishedVersion, int? VerifiedVersion)
{
    /// <summary>Always true: Stav obsahu lists published pages only (only a publication can be verified).</summary>
    public bool IsPublished => true;
}

/// <param name="Enabled">Whether the space verifies its pages; off: the list still shows the readership.</param>
public sealed record ContentHealthList(bool Enabled, ContentHealthSummary Summary, IReadOnlyList<ContentHealthItem> Items, int Total, string? NextCursor);
