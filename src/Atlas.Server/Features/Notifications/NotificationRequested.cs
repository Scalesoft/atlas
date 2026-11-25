using System.Text.Json.Serialization;
using Atlas.Server.Platform.Events;

namespace Atlas.Server.Features.Notifications;

/// <summary>The kinds of the inbox (inbox spec §1); a preference names one of them.</summary>
public static class NotificationKinds
{
    public const string CommentMention = "comment.mention";
    public const string PageMention = "page.mention";
    /// <summary>A personal item shared with the recipient (a person or a member of the group): SetItemGrantHandler.</summary>
    public const string ItemShared = "item.shared";
    /// <summary>The recipient's share of a personal item changed its level or validity: SetItemGrantHandler.</summary>
    public const string ItemShareChanged = "item.share_changed";
    /// <summary>The recipient's share of a personal item ended: SetItemGrantHandler and „Odebrat vše“ (RevokeAllAccessHandler).</summary>
    public const string ItemUnshared = "item.unshared";
    /// <summary>A background job failed for good (3.3 plan §4.3), to the wiki administrators: the subject is <see cref="JobSubject"/>,
    /// the anchor the job id. Shown to whoever is a wiki administrator when reading.</summary>
    public const string JobFailed = "job.failed";
    /// <summary>5.2: the topic (and preference) of the steward's reminders (ContentVerificationJob): one item per page, the later stage
    /// replaces the earlier one, as a share's change replaces its share.</summary>
    public const string VerificationDue = "page.verification_due";
    /// <summary>The page's verification expires within 14 days (topic <see cref="VerificationDue"/>).</summary>
    public const string VerificationExpiring = "page.verification_expiring";
    /// <summary>The page's verification expired (topic <see cref="VerificationDue"/>).</summary>
    public const string VerificationExpired = "page.verification_expired";
    /// <summary>5.2 „Nahlásit neaktuální“: a reader reports the page outdated to its steward (else up to three editors who last published
    /// it); the reader's note is stored in the anchor and read as the item's excerpt; repeated reports aggregate into one item.</summary>
    public const string ReportedOutdated = "page.reported_outdated";
    /// <summary>5.2 (plan §2.3): the actor's deactivated account was the steward of verified pages of the space (the subject), told to its
    /// administrators once per space (topic <see cref="VerificationDue"/>); shown while the reader administers the space.</summary>
    public const string StewardDeactivated = "space.steward_deactivated";

    /// <summary>How long the same actor's repeat of the kind on one item neither counts nor makes it unread again: a reader who reports a
    /// page again and again does not re-raise the steward's item (5.2 review L1). Zero for the other kinds.</summary>
    public static TimeSpan QuietPeriod(string kind) => kind == ReportedOutdated ? TimeSpan.FromMinutes(10) : TimeSpan.Zero;

    /// <summary>The kinds a preference names; a share's change and end follow the switch of <see cref="ItemShared"/>.</summary>
    public static readonly IReadOnlyList<string> All = [CommentMention, PageMention, ItemShared, VerificationDue, ReportedOutdated];

    /// <summary>The preference and aggregation topic of a kind: the three share kinds are one topic, so a share that changes or ends
    /// replaces its earlier item instead of leaving it beside the new one.</summary>
    public static string Topic(string kind) => kind is ItemShareChanged or ItemUnshared ? ItemShared
        : kind is VerificationExpiring or VerificationExpired or StewardDeactivated ? VerificationDue
        : kind;

    /// <summary>The subject of a job's failures: one per job id, so they aggregate into one item.</summary>
    public static Guid JobSubject(string jobId)
        => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("atlas.job:" + jobId)).AsSpan(0, 16));
}

/// <summary>
/// A producer's request to tell <see cref="RecipientIds"/> that <see cref="ActorId"/> did <see cref="Kind"/> on the object
/// <see cref="SubjectId"/> (inbox spec §3): an outbox message, so it is written with the producer's own transaction and delivered at
/// least once. One item per recipient and <see cref="Key"/>: a later request of the same key aggregates into it (§4).
/// </summary>
/// <param name="Anchor">A heading id of a page or an item id of a board the recipient's link opens at; null for the object.</param>
public sealed record NotificationRequested(string Kind, Guid SubjectId, Guid? ActorId, IReadOnlyList<Guid> RecipientIds, string? Anchor = null)
    : IOutboxMessage
{
    [JsonIgnore]
    public string Key => $"{NotificationKinds.Topic(Kind)}:{SubjectId:D}";
}

public static class NotificationOutbox
{
    /// <summary>Enqueues the request in the current handler's transaction (nothing for no recipients), ordered by its key, so the
    /// consumer counts a repeated delivery once (inbox spec §4). The one call a producer makes.</summary>
    public static void Notify(this Outbox outbox, NotificationRequested request)
    {
        if (request.RecipientIds.Count > 0)
        {
            outbox.Enqueue(request, "inbox:" + request.Key);
        }
    }
}
