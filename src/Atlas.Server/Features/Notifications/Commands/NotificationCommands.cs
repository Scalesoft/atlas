using Atlas.Server.Features.Notifications.Contracts;
using Atlas.Server.Platform.Live;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Notifications.Commands;

/// <summary>Marks one item of the caller's inbox read, or with <see cref="NotificationId"/> null all of them.</summary>
public sealed record MarkNotificationsRead(Guid? NotificationId);

/// <summary>Switches the caller's notifications of a kind on or off (inbox spec §7).</summary>
public sealed record SetNotificationPreference(string Kind, bool Enabled);

/// <summary>Only the caller's own items: another's, or a missing one, is 404 (inbox spec §5). Hidden items are marked too, which tells
/// nothing. The caller's other tabs hear it on the account's channel.</summary>
public sealed class MarkNotificationsReadHandler(IContentAccess access, ISession session, NotificationReads reads, AccessChanges changes, LiveRelay relay)
{
    public async Task<NotificationsMarked> Handle(MarkNotificationsRead command, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        var marked = await session.CreateSQLQuery("""
                UPDATE "Notification" SET "ReadAt"=coalesce("ReadAt",now())
                WHERE "RecipientId"=:recipient AND (CAST(:id AS uuid) IS NULL AND "ReadAt" IS NULL OR "Id"=:id)
                """)
            .SetParameter("recipient", actor.Id).SetParameter("id", command.NotificationId, NHibernateUtil.Guid).ExecuteUpdateAsync(ct);
        if (command.NotificationId is not null && marked == 0)
        {
            throw new ApiException(404, "Oznámení nebylo nalezeno.", "notification_not_found");
        }
        changes.AfterCommit(() => NotificationsChanged.PublishAsync(relay, actor.Id));
        return new NotificationsMarked(marked, await reads.UnreadAsync(actor, ct));
    }
}

public sealed class SetNotificationPreferenceHandler(IContentAccess access, ISession session)
{
    public async Task<NotificationPreferenceDto> Handle(SetNotificationPreference command, CancellationToken ct)
    {
        if (!NotificationKinds.All.Contains(command.Kind))
        {
            throw new ApiException(400, "Neznámý druh oznámení.", "invalid_notification_kind");
        }
        var actor = await access.LockActorAsync(null, ct);
        await session.CreateSQLQuery(command.Enabled
                ? """DELETE FROM "NotificationPreference" WHERE "UserId"=:user AND "Kind"=:kind"""
                : """INSERT INTO "NotificationPreference"("UserId","Kind") VALUES(:user,:kind) ON CONFLICT DO NOTHING""")
            .SetParameter("user", actor.Id).SetParameter("kind", command.Kind).ExecuteUpdateAsync(ct);
        return new NotificationPreferenceDto(command.Kind, command.Enabled);
    }
}
