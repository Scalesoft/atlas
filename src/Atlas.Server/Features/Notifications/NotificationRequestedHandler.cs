using Atlas.Server.Platform.Live;
using NHibernate;
using Wolverine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Notifications;

/// <summary>
/// Delivers <see cref="NotificationRequested"/> into the recipients' inboxes in one statement (inbox spec §3, §4): not to the actor,
/// an inactive or missing account or one that switched the kind's topic off. An unread item of the key counts one more event and takes
/// the latest kind of its topic, a read one is unread again with one; the same actor's repeat within the kind's
/// <see cref="NotificationKinds.QuietPeriod"/> changes nothing, and a report without a note keeps the earlier note. The message the item counted last (LastMessageId, its envelope's
/// id) changes nothing when it comes again, as a retry after a lost commit or a replayed dead letter does. Another message counts in
/// whatever order the queue brings it. After the commit each recipient's own channel hears that its inbox changed.
/// </summary>
public sealed class NotificationRequestedHandler(ISession session, AccessChanges changes, LiveRelay relay)
{
    public async Task Handle(NotificationRequested message, Envelope envelope, CancellationToken ct)
    {
        var recipients = await session.CreateSQLQuery("""
                INSERT INTO "Notification" AS n ("Id","RecipientId","Kind","SubjectId","Anchor","AggregationKey","ActorId","EventCount","LastMessageId","CreatedAt","UpdatedAt")
                SELECT gen_random_uuid(),u."Id",:kind,:subject,:anchor,:key,:actor,1,:message,now(),now()
                FROM "UserAccount" u
                WHERE u."Id"=ANY(CAST(string_to_array(:recipients, ',') AS uuid[])) AND u."IsActive" AND u."Id" IS DISTINCT FROM :actor
                  AND NOT EXISTS (SELECT 1 FROM "NotificationPreference" p WHERE p."UserId"=u."Id" AND p."Kind"=:topic)
                ON CONFLICT ("RecipientId","AggregationKey") DO UPDATE SET
                  "Kind"=EXCLUDED."Kind","EventCount"=CASE WHEN n."ReadAt" IS NULL THEN n."EventCount"+1 ELSE 1 END,
                  "SubjectId"=EXCLUDED."SubjectId","Anchor"=CASE WHEN EXCLUDED."Kind"=:noted THEN coalesce(EXCLUDED."Anchor",n."Anchor") ELSE EXCLUDED."Anchor" END,"ActorId"=EXCLUDED."ActorId",
                  "LastMessageId"=EXCLUDED."LastMessageId","UpdatedAt"=now(),"ReadAt"=NULL
                WHERE n."LastMessageId" IS DISTINCT FROM :message
                  AND NOT (n."Kind"=EXCLUDED."Kind" AND n."ActorId" IS NOT DISTINCT FROM EXCLUDED."ActorId" AND n."UpdatedAt">now()-:quiet*interval '1 second')
                RETURNING n."RecipientId"
                """)
            .SetParameter("kind", message.Kind).SetParameter("topic", NotificationKinds.Topic(message.Kind)).SetParameter("subject", message.SubjectId).SetParameter("anchor", message.Anchor, NHibernateUtil.String)
            .SetParameter("key", message.Key).SetParameter("actor", message.ActorId, NHibernateUtil.Guid)
            .SetParameter("noted", NotificationKinds.ReportedOutdated).SetParameter("quiet", (int)NotificationKinds.QuietPeriod(message.Kind).TotalSeconds).SetParameter("message", envelope.Id).SetParameter("recipients", string.Join(',', message.RecipientIds.Distinct()))
            .ListAsync<Guid>(ct);
        foreach (var recipient in recipients)
        {
            changes.AfterCommit(() => NotificationsChanged.PublishAsync(relay, recipient));
        }
    }
}
