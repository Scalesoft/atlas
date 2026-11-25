namespace Atlas.Server.Features.PageVerifications.Domain;

/// <summary>
/// 5.2 Ověřený obsah (docs/atlas-5.2-plan.md §2.1): the page's steward (garant), its own interval and its last verification. A setting,
/// not content: never serialized with the page (wiki archive); the installation backup carries the table. One row per page, written at
/// its first setting; a page without one has no steward, inherits the space's interval and was never verified.
/// </summary>
public class PageVerification
{
    public virtual Guid PageId { get; set; }
    public virtual Guid? StewardId { get; set; }
    /// <summary>Null inherits the space's <see cref="Spaces.Domain.Space.VerificationIntervalMonths"/>; 0 = the page is not verified.</summary>
    public virtual short? IntervalMonths { get; set; }
    public virtual Guid? VerifiedById { get; set; }
    public virtual DateTime? VerifiedAt { get; set; }
    public virtual DateTime? VerifiedUntil { get; set; }
    /// <summary>The published version that was verified.</summary>
    public virtual int? VerifiedVersion { get; set; }
    /// <summary>The channel the verification came through (ContentChannels): over MCP, the API or the assistant it is „přes AI“.</summary>
    public virtual string? VerifiedChannel { get; set; }
    /// <summary>The last notice sent: <see cref="PageVerificationStates.NoticeNone"/>, <see cref="PageVerificationStates.NoticeExpiring"/>
    /// or <see cref="PageVerificationStates.NoticeExpired"/>.</summary>
    public virtual short NoticeStage { get; set; }
}

/// <summary>The derived state of a page's verification (plan §2.2): computed here, in one place, never stored and never in SQL.</summary>
public static class PageVerificationStates
{
    public const string Off = "off";
    public const string Unverified = "unverified";
    public const string Verified = "verified";
    public const string Expiring = "expiring";
    public const string Expired = "expired";

    public const short NoticeNone = 0, NoticeExpiring = 1, NoticeExpired = 2;

    /// <summary>A verification that ends within this time is „Brzy vyprší“; the job tells the steward then (O3).</summary>
    public static readonly TimeSpan Warning = TimeSpan.FromDays(14);

    /// <summary>The intervals a space or a page may choose; 0 on a page = „Neověřovat“.</summary>
    public static readonly IReadOnlySet<int> Intervals = new HashSet<int> { 3, 6, 12 };

    /// <summary>The interval in effect: the page's own, else the space's; null or 0 = off. A template is never verified.</summary>
    public static int? Interval(short? own, short? space, bool isTemplate) => isTemplate ? null : own ?? space;

    public static string Of(int? interval, DateTime? until, DateTime now) =>
        interval is null or 0 ? Off
        : until is not { } end ? Unverified
        : end <= now ? Expired
        : end <= now + Warning ? Expiring
        : Verified;

    /// <summary>The states „k ověření“ of Stav obsahu.</summary>
    public static bool IsDue(string state) => state is Unverified or Expiring or Expired;
}
