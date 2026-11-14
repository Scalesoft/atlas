using Atlas.Server.Features.Notifications.Contracts;

namespace Atlas.Server.Features.Notifications.Queries;

/// <summary>The caller's inbox, newest event first, <see cref="NotificationReads.PageSize"/> rows a page after the item <see cref="Before"/>.</summary>
public sealed record ListNotifications(Guid? Before);

public sealed record GetUnreadNotifications;

public sealed record GetNotificationPreferences;

public sealed class ListNotificationsHandler(IContentAccess access, NotificationReads reads)
{
    public async Task<NotificationList> Handle(ListNotifications query, CancellationToken ct)
        => await reads.PageAsync(await access.LockActorAsync(null, ct), query.Before, ct);
}

public sealed class GetUnreadNotificationsHandler(IContentAccess access, NotificationReads reads)
{
    public async Task<UnreadNotifications> Handle(GetUnreadNotifications query, CancellationToken ct)
        => new(await reads.UnreadAsync(await access.LockActorAsync(null, ct), ct));
}

/// <summary>Every kind with whether the caller receives it (inbox spec §7): on unless switched off.</summary>
public sealed class GetNotificationPreferencesHandler(IContentAccess access, NHibernate.ISession session)
{
    public async Task<NotificationPreferences> Handle(GetNotificationPreferences query, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        var off = await session.CreateSQLQuery("""SELECT "Kind" FROM "NotificationPreference" WHERE "UserId"=:user""").SetParameter("user", actor.Id).ListAsync<string>(ct);
        return new NotificationPreferences(NotificationKinds.All.Select(kind => new NotificationPreferenceDto(kind, !off.Contains(kind))).ToList());
    }
}
