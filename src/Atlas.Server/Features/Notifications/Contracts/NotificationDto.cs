namespace Atlas.Server.Features.Notifications.Contracts;

/// <summary>An inbox item as its recipient may see it now (inbox spec §5): the subject's current title and space under the recipient's
/// rights, who did it last and how many events it holds since it was last read. An ended share the recipient may not read any more
/// has no type, title, space or anchor. A mention in a comment names the newest comment of that author that mentions the recipient
/// (<c>CommentId</c>, for the link) and its plain-text <c>Excerpt</c>; none once that comment is deleted.</summary>
public sealed record NotificationDto(Guid Id, string Kind, Guid SubjectId, string? SubjectType, string SubjectTitle, Guid? SpaceId, string? SpaceName,
    string? Anchor, Guid? ActorId, string? ActorName, int Count, DateTime CreatedAt, DateTime UpdatedAt, bool Read, Guid? CommentId = null, string? Excerpt = null);

/// <param name="Next">The cursor of the next page (the page's last item); null at the end.</param>
/// <param name="Unread">The visible unread items, at most <c>NotificationReads.UnreadWindow</c>.</param>
public sealed record NotificationList(IReadOnlyList<NotificationDto> Items, Guid? Next, int Unread);

public sealed record UnreadNotifications(int Count);

public sealed record NotificationPreferenceDto(string Kind, bool Enabled);

public sealed record NotificationPreferences(IReadOnlyList<NotificationPreferenceDto> Items);

public sealed record SetNotificationPreferenceRequest(bool Enabled);

/// <summary>How many items a mark-read changed, and the visible unread count after it.</summary>
public sealed record NotificationsMarked(int Marked, int Unread);
