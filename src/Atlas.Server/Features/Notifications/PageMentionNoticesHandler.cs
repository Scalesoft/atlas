using System.Text.Json.Nodes;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Events;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Notifications;

/// <summary>The mention notices' own delivery of <see cref="ContentRevised"/>: publications and the catalog's silent <c>rebuild</c> of pages
/// (ARCH2-1); not coalesced, since only a publication tells anybody.</summary>
public sealed record PageMentionRevision(ContentRevised Event) : IOutboxDelivery<ContentRevised>
{
    public static bool Takes(ContentRevised message) => message is { ContentType: "atlas.page", RevisionKind: "published" or "rebuild" };
}

/// <summary>
/// The producer of <see cref="NotificationKinds.PageMention"/> (inbox spec §4): on a page's publication (ContentRevised
/// <c>published</c>, which every path of publication announces), the accounts its published revision mentions now and no
/// publication had told yet, told by the current publication's publisher. The mentions are the <c>internalUser</c> of the revision's
/// contact snapshots as the one reader of them (<see cref="ContactSnapshots"/>) finds them, so the inbox, search and the entity catalog
/// agree on what a page mentions. Rebuilt from the page's current state: a repeated or late delivery finds nobody new, a mention removed
/// is forgotten (a later one tells again), a trashed, unpublished or template page changes nothing. Working revisions never tell
/// anybody. The catalog's <c>rebuild</c> after a restore (atlas_enqueue_entity_catalog) records what the page mentions, trashed or not,
/// and tells nobody, so a restore never tells anybody again; so does the archive import's, which announces its pages as
/// <c>rebuild</c> (WikiBackupImport), and a <c>published</c> event without a revision id from before 2026102985.
/// </summary>
public sealed class PageMentionNoticesHandler(ISession session, Outbox outbox)
{
    public async Task Handle(PageMentionRevision delivery, CancellationToken ct)
    {
        var message = delivery.Event;
        if (!PageMentionRevision.Takes(message))
        {
            return;
        }
        var tell = message is { RevisionKind: "published", RevisionId: not null };
        var page = await session.CreateSQLQuery("""
                SELECT r."ContentJson" AS rich, coalesce(pp."PublisherId", r."AuthorId") AS actor, NOT p."IsTrashed" AND NOT p."IsTemplate" AS live
                FROM "Page" p JOIN "PageRevision" r ON r."Id"=p."PublishedRevisionId"
                LEFT JOIN "PagePublication" pp ON pp."PageId"=p."Id" AND pp."ActiveTo" IS NULL
                WHERE p."Id"=:page
                """)
            .AddScalar("rich", NHibernateUtil.String).AddScalar("actor", NHibernateUtil.Guid).AddScalar("live", NHibernateUtil.Boolean)
            .SetParameter("page", message.ResourceId)
            .UniqueResultAsync<object[]>(ct);
        if (page is null || tell && !(bool)page[2])
        {
            return;
        }
        var mentioned = ContactSnapshots.Read((string)page[0])
            .Select(x => x.Contact["internalUser"] is JsonObject user && user["id"] is JsonValue id && id.TryGetValue<string>(out var text)
                && Guid.TryParseExact(text, "D", out var account) ? account : Guid.Empty)
            .Where(account => account != Guid.Empty).Distinct();
        var added = await session.CreateSQLQuery("""
                WITH mentioned AS (
                  SELECT u."Id" FROM "UserAccount" u WHERE u."Id"=ANY(CAST(string_to_array(:mentioned, ',') AS uuid[]))
                ), gone AS (
                  DELETE FROM "PageMentionNotice" n WHERE n."PageId"=:page AND n."UserId" NOT IN (SELECT "Id" FROM mentioned)
                )
                INSERT INTO "PageMentionNotice"("PageId","UserId") SELECT :page,"Id" FROM mentioned ON CONFLICT DO NOTHING RETURNING "UserId"
                """)
            .AddScalar("UserId", NHibernateUtil.Guid)
            .SetParameter("page", message.ResourceId).SetParameter("mentioned", string.Join(',', mentioned))
            .ListAsync<Guid>(ct);
        if (tell && added.Count > 0)
        {
            outbox.Notify(new NotificationRequested(NotificationKinds.PageMention, message.ResourceId, (Guid)page[1], added.ToList()));
        }
    }
}
