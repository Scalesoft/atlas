using System.Globalization;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Access.Commands;
using Atlas.Server.Features.Comments.Commands;
using Atlas.Server.Features.Entities.Commands;
using Atlas.Server.Features.Notifications;
using Atlas.Server.Features.Notifications.Commands;
using Atlas.Server.Features.Notifications.Contracts;
using Atlas.Server.Features.Notifications.Queries;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.PersonalItems;
using Atlas.Server.Features.PersonalItems.Commands;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Live;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Comments;
using Atlas.Server.Tests.Hub;
using Atlas.Server.Tests.Jobs;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Xunit;

namespace Atlas.Server.Tests.Notifications;

/// <summary>
/// The inbox through its producers, the outbox and its reads (inbox spec §3–§7): aggregation per object, a repeated delivery counted
/// once, edits that tell nobody, the recipient's current rights at every read, preferences, retention and the live signal.
/// </summary>
public sealed class NotificationTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Comment_mentions_aggregate_per_object_tell_nobody_on_an_edit_and_come_back_unread_after_reading()
    {
        await using var f = await CommentsFixture.CreateAsync(outbox: true);
        var board = await f.BoardAsync();
        var live = f.Boards.SubscribeTo(f.Reader, UserLiveChannel.Name(f.Reader.Id), false);
        var mark = f.Mark();

        var first = await f.PostAsync(f.Writer, board, "Ahoj " + Mention(f.Reader), anchor: "frame-1");
        await DrainAsync(f);
        var item = Assert.Single((await InboxAsync(f, f.Reader)).Items);
        Assert.Equal((NotificationKinds.CommentMention, board, "atlas.board", "Plan", "frame-1", f.Writer.Id, "writer", 1, false),
            (item.Kind, item.SubjectId, item.SubjectType, item.SubjectTitle, item.Anchor, item.ActorId, item.ActorName, item.Count, item.Read));
        Assert.NotEmpty(f.Since<NotificationsChanged>(mark, live, NotificationsChanged.Event));

        // Another comment of the object aggregates into the item; the author never hears of a self-mention; an edit that keeps the
        // mention tells nobody.
        await f.PostAsync(f.Manager, board, Mention(f.Reader) + " a " + Mention(f.Manager));
        await f.As(f.Writer, new EditComment(board, first.Id, "Ahoj znovu " + Mention(f.Reader)));
        await DrainAsync(f);
        var inbox = await InboxAsync(f, f.Reader);
        item = Assert.Single(inbox.Items);
        Assert.Equal((2, f.Manager.Id, 1), (item.Count, item.ActorId, inbox.Unread));
        Assert.Empty((await InboxAsync(f, f.Manager)).Items);

        var marked = await f.As<NotificationsMarked>(f.Reader, new MarkNotificationsRead(item.Id));
        Assert.Equal((1, 0), (marked.Marked, marked.Unread));
        Assert.True(Assert.Single((await InboxAsync(f, f.Reader)).Items).Read);
        await BoardsFixture.Refused(404, () => f.As<NotificationsMarked>(f.Writer, new MarkNotificationsRead(item.Id)), "notification_not_found");

        // A new event brings a read item back, counting from one.
        await f.PostAsync(f.Writer, board, Mention(f.Reader));
        await DrainAsync(f);
        item = Assert.Single((await InboxAsync(f, f.Reader)).Items);
        Assert.Equal((1, false, f.Writer.Id), (item.Count, item.Read, item.ActorId));
        Assert.Equal(new UnreadNotifications(1), await f.As<UnreadNotifications>(f.Reader, new GetUnreadNotifications()));
        Assert.Equal(1, (await f.As<NotificationsMarked>(f.Reader, new MarkNotificationsRead(null))).Marked);
    }

    /// <summary>A repeated delivery of the message counted last (the same envelope id) counts nothing; any other message counts, in
    /// whatever order it arrives (PER1-3: two comments of one object lock nothing in common).</summary>
    [PostgreSqlFact]
    public async Task A_repeated_delivery_counts_nothing_while_another_and_an_inline_request_count()
    {
        await using var f = await CommentsFixture.CreateAsync(outbox: true);
        var board = await f.BoardAsync();
        var request = new NotificationRequested(NotificationKinds.CommentMention, board, f.Writer.Id, [f.Reader.Id, f.Reader.Id, Guid.NewGuid()]);

        await DeliverAsync(f, request);
        Assert.Equal(1, Assert.Single((await InboxAsync(f, f.Reader)).Items).Count);
        // Wolverine recovers the delivered envelope as after a crash before it was marked handled: the same id comes again.
        await Atlas.Server.Tests.Events.OutboxTests.RecoverAsync(f.Host);
        Assert.Equal(1, Assert.Single((await InboxAsync(f, f.Reader)).Items).Count);
        await DeliverAsync(f, request);
        await f.Host.InvokeAsync(request);
        Assert.Equal(3, Assert.Single((await InboxAsync(f, f.Reader)).Items).Count);
    }

    /// <summary>SEC1-1: anybody can fill another's inbox with items the recipient may not read (share, then trash). They are filtered
    /// before the page's limit and before the count, so they neither hide a readable item nor leave an empty first page.</summary>
    [PostgreSqlFact]
    public async Task Unreadable_items_neither_hide_readable_ones_from_the_count_nor_from_the_pages()
    {
        await using var f = await CommentsFixture.CreateAsync(outbox: true);
        var board = await f.BoardAsync();
        await f.PostAsync(f.Writer, board, Mention(f.Reader));
        await DrainAsync(f);
        // 150 newer unread items of subjects that do not exist (as purged ones), interleaved with 35 ended shares, which show without
        // their subject.
        await f.Host.ExecuteAsync("""
            INSERT INTO "Notification"("Id","RecipientId","Kind","SubjectId","AggregationKey","CreatedAt","UpdatedAt")
            SELECT gen_random_uuid(),:id,CASE WHEN i%5=0 AND i<=175 THEN 'item.unshared' ELSE 'item.shared' END,gen_random_uuid(),'flood:'||i,now(),
              now() + make_interval(secs => i)
            FROM generate_series(1,185) i
            """, f.Reader.Id);

        var first = await InboxAsync(f, f.Reader);
        Assert.Equal((30, 36), (first.Items.Count, first.Unread));
        Assert.All(first.Items, item => Assert.Equal(NotificationKinds.ItemUnshared, item.Kind));
        Assert.Equal(first.Items[^1].Id, first.Next);
        var second = await f.As<NotificationList>(f.Reader, new ListNotifications(first.Next));
        Assert.Equal((6, board, (Guid?)null), (second.Items.Count, second.Items[^1].SubjectId, second.Next));
        Assert.Equal(new UnreadNotifications(36), await f.As<UnreadNotifications>(f.Reader, new GetUnreadNotifications()));
    }

    [PostgreSqlFact]
    public async Task A_recipient_without_access_now_sees_no_trace_of_the_item()
    {
        await using var f = await CommentsFixture.CreateAsync(outbox: true);
        var board = await f.BoardAsync();
        await f.PostAsync(f.Writer, board, Mention(f.Reader) + " " + Mention(f.Outsider));
        await DrainAsync(f);
        Assert.Single((await InboxAsync(f, f.Reader)).Items);
        // Never had access: the row exists, the inbox shows neither it nor its count.
        Assert.Equal((0, 0), ((await InboxAsync(f, f.Outsider)).Items.Count, (await InboxAsync(f, f.Outsider)).Unread));
        Assert.Equal(1L, await ScalarAsync(f, f.Outsider.Id, """SELECT count(*) FROM "Notification" WHERE "RecipientId"=:id"""));

        await f.Host.ExecuteAsync("""UPDATE "SpaceGrant" SET "ActiveTo"=clock_timestamp() WHERE "UserId"=:id AND "ActiveTo" IS NULL""", f.Reader.Id);
        var inbox = await InboxAsync(f, f.Reader);
        Assert.Equal((0, 0), (inbox.Items.Count, inbox.Unread));
        Assert.Equal(new UnreadNotifications(0), await f.As<UnreadNotifications>(f.Reader, new GetUnreadNotifications()));
    }

    [PostgreSqlFact]
    public async Task A_kind_switched_off_is_not_delivered_and_an_unknown_kind_is_refused()
    {
        await using var f = await CommentsFixture.CreateAsync(outbox: true);
        var board = await f.BoardAsync();
        await f.As<NotificationPreferenceDto>(f.Reader, new SetNotificationPreference(NotificationKinds.CommentMention, false));
        Assert.Equal([(NotificationKinds.CommentMention, false), (NotificationKinds.PageMention, true), (NotificationKinds.ItemShared, true),
            (NotificationKinds.VerificationDue, true), (NotificationKinds.ReportedOutdated, true)],
            (await f.As<NotificationPreferences>(f.Reader, new GetNotificationPreferences())).Items.Select(x => (x.Kind, x.Enabled)));
        await BoardsFixture.Refused(400, () => f.As<NotificationPreferenceDto>(f.Reader, new SetNotificationPreference("spam", false)), "invalid_notification_kind");

        await f.PostAsync(f.Writer, board, Mention(f.Reader));
        await DrainAsync(f);
        Assert.Empty((await InboxAsync(f, f.Reader)).Items);

        await f.As<NotificationPreferenceDto>(f.Reader, new SetNotificationPreference(NotificationKinds.CommentMention, true));
        await f.PostAsync(f.Writer, board, Mention(f.Reader));
        await DrainAsync(f);
        Assert.Single((await InboxAsync(f, f.Reader)).Items);
    }

    [PostgreSqlFact]
    public async Task A_page_tells_its_mentions_at_publication_only_to_the_newly_mentioned()
    {
        await using var f = await CommentsFixture.CreateAsync(outbox: true);
        var pages = new TestPages(f.Host);
        var page = await pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Plán", Rich(f.Reader), null), f.Writer, default);
        await DrainAsync(f);
        Assert.Empty((await InboxAsync(f, f.Reader)).Items);

        var published = await pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        await DrainAsync(f);
        var item = Assert.Single((await InboxAsync(f, f.Reader)).Items);
        Assert.Equal((NotificationKinds.PageMention, page.Id, "Plán", f.Writer.Id, 1), (item.Kind, item.SubjectId, item.SubjectTitle, item.ActorId, item.Count));

        // A new mention tells only its account; the reader, still mentioned, hears nothing again.
        var saved = await pages.SaveAsync(page.Id, new SavePageRequest("Plán", Rich(f.Reader, f.Manager), null, published.Version), f.Writer, default);
        await DrainAsync(f);
        Assert.Empty((await InboxAsync(f, f.Manager)).Items);
        await pages.PublishAsync(page.Id, new PublishPageRequest(saved.Version), f.Writer, default);
        await DrainAsync(f);
        Assert.Equal(1, Assert.Single((await InboxAsync(f, f.Reader)).Items).Count);
        Assert.Single((await InboxAsync(f, f.Manager)).Items);
        Assert.Equal(2L, await ScalarAsync(f, page.Id, """SELECT count(*) FROM "PageMentionNotice" WHERE "PageId"=:id"""));
    }

    /// <summary>ARCH1-5: the mentions are read by ContactSnapshots, the entity catalog's reader, without an SQL reader of their own.
    /// After a restore the catalog's rebuild (published without a revision) records them and tells nobody, so the next publication
    /// tells only the newly mentioned.</summary>
    [PostgreSqlFact]
    public async Task The_catalog_rebuild_after_a_restore_records_page_mentions_without_telling()
    {
        await using var f = await CommentsFixture.CreateAsync(outbox: true);
        var pages = new TestPages(f.Host);
        var page = await pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Plán", Rich(f.Reader), null), f.Writer, default);
        var published = await pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        await DrainAsync(f);
        Assert.Single((await InboxAsync(f, f.Reader)).Items);

        // As a restore leaves it: nothing told yet; the catalog's rebuild message records the current mentions without telling.
        await f.Host.ExecuteAsync("""DELETE FROM "PageMentionNotice" WHERE "PageId"=:id""", page.Id);
        await f.Host.ExecuteAsync("""DELETE FROM "Notification" WHERE "RecipientId"=:id""", f.Reader.Id);
        // Through the real path (ARCH2-1 after the per-consumer outbox): the rebuild announces, the outbox sends the mentions' delivery.
        var tracked = await f.Host.TrackAsync(() => f.Host.InvokeAsync<int>(new AnnounceCatalogRebuild()));
        Assert.Equal(["rebuild"], tracked.Sent.MessagesOf<PageMentionRevision>().Where(delivery => delivery.Event.ResourceId == page.Id)
            .Select(delivery => delivery.Event.RevisionKind));
        await DrainAsync(f);
        Assert.Empty((await InboxAsync(f, f.Reader)).Items);
        Assert.Equal(1L, await ScalarAsync(f, page.Id, """SELECT count(*) FROM "PageMentionNotice" WHERE "PageId"=:id"""));

        var saved = await pages.SaveAsync(page.Id, new SavePageRequest("Plán", Rich(f.Reader, f.Manager), null, published.Version), f.Writer, default);
        await pages.PublishAsync(page.Id, new PublishPageRequest(saved.Version), f.Writer, default);
        await DrainAsync(f);
        Assert.Empty((await InboxAsync(f, f.Reader)).Items);
        Assert.Single((await InboxAsync(f, f.Manager)).Items);
    }

    /// <summary>A share, its change and its end are one item per recipient and personal item; a group share tells the group's members; an
    /// ended share stays readable as an event but tells nothing of the item; „Odebrat vše“ ends shares too.</summary>
    [PostgreSqlFact]
    public async Task Shares_of_a_personal_item_tell_their_recipients_and_an_ended_share_names_no_item()
    {
        await using var f = await CommentsFixture.CreateAsync(outbox: true);
        var member = await f.Host.SeedUserAsync("member");
        var group = await f.Host.SeedGroupAsync("Tým", member, f.Writer);
        var note = await f.As<ObjectDto>(f.Writer, new CreateObject(NoteObjectType.Id, ObjectScopes.Private, null, new JsonObject { ["title"] = "Tajný plán", ["text"] = "x" }));

        await f.As(f.Writer, new SetItemGrant(note.Id, f.Reader.Id, null, 1, null, null, null));
        await f.As(f.Writer, new SetItemGrant(note.Id, null, group, 1, null, null, null));
        await DrainAsync(f);
        var shared = Assert.Single((await InboxAsync(f, f.Reader)).Items);
        Assert.Equal((NotificationKinds.ItemShared, (string?)"atlas.note", "Tajný plán", (Guid?)f.Writer.Id), (shared.Kind, shared.SubjectType, shared.SubjectTitle, shared.ActorId));
        Assert.Equal(NotificationKinds.ItemShared, Assert.Single((await InboxAsync(f, member)).Items).Kind);
        // The owner, a member of the group, is never told of their own share.
        Assert.Empty((await InboxAsync(f, f.Writer)).Items);

        await f.As(f.Writer, new SetItemGrant(note.Id, f.Reader.Id, null, 2, null, null, await GrantVersionAsync(f, note.Id, f.Reader.Id)));
        await DrainAsync(f);
        var changed = Assert.Single((await InboxAsync(f, f.Reader)).Items);
        Assert.Equal((NotificationKinds.ItemShareChanged, 2, "Tajný plán"), (changed.Kind, changed.Count, changed.SubjectTitle));

        // Ended: the same item, now without the item's type, title or space.
        await f.As(f.Writer, new SetItemGrant(note.Id, f.Reader.Id, null, null, null, null, await GrantVersionAsync(f, note.Id, f.Reader.Id)));
        await DrainAsync(f);
        var inbox = await InboxAsync(f, f.Reader);
        var ended = Assert.Single(inbox.Items);
        Assert.Equal((NotificationKinds.ItemUnshared, (string?)null, "", (string?)null, 1), (ended.Kind, ended.SubjectType, ended.SubjectTitle, ended.SpaceName, inbox.Unread));

        // „Odebrat vše“ closes the account's own grants (its group's stay with the group, but the group membership ends too).
        await f.As(f.Writer, new SetItemGrant(note.Id, member.Id, null, 1, null, null, null));
        await f.As(f.Admin, new RevokeAllAccess(member.Id));
        await DrainAsync(f);
        ended = Assert.Single((await InboxAsync(f, member)).Items);
        Assert.Equal((NotificationKinds.ItemUnshared, (Guid?)f.Admin.Id, ""), (ended.Kind, ended.ActorId, ended.SubjectTitle));
    }

    /// <summary>A failed job tells the wiki administrators as JobAccess decides them, Hub's included (review 3.5.1 #7): a Hub-only
    /// administrator receives and reads the item, and no longer sees it once the Hub grant lapses.</summary>
    [PostgreSqlFact]
    public async Task A_failed_job_tells_a_hub_administrator_who_no_longer_sees_it_once_the_grant_lapses()
    {
        await using var f = await CommentsFixture.CreateAsync(services => services.AddJob<FatalJob>(), outbox: true);
        var (hubAdmin, _, identity) = await HubEffectiveAuthorityTests.SeedAsync(f.Host, true);
        Assert.False(hubAdmin.IsWikiAdmin);

        await f.Host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(FatalJob.JobId, "Test");
        await DrainAsync(f);
        var inbox = await InboxAsync(f, hubAdmin);
        var item = Assert.Single(inbox.Items);
        Assert.Equal((NotificationKinds.JobFailed, FatalJob.JobId, 1), (item.Kind, item.Anchor, inbox.Unread));

        await HubEffectiveAuthorityTests.ExpireAsync(f.Host, identity);
        var lapsed = await InboxAsync(f, hubAdmin);
        Assert.Equal((0, 0), (lapsed.Items.Count, lapsed.Unread));
    }

    [PostgreSqlFact]
    public async Task Retention_removes_read_items_after_ninety_days_and_any_after_a_year()
    {
        await using var f = await CommentsFixture.CreateAsync();
        foreach (var (key, age, read) in new[] { ("a", 100, true), ("b", 100, false), ("c", 400, false), ("d", 10, true) })
        {
            await f.Host.ExecuteAsync($"""
                INSERT INTO "Notification"("Id","RecipientId","Kind","SubjectId","AggregationKey","CreatedAt","UpdatedAt","ReadAt")
                VALUES(gen_random_uuid(),:id,'comment.mention',gen_random_uuid(),'{key}',now(),now() - interval '{age} days',{(read ? "now()" : "NULL")})
                """, f.Reader.Id);
        }
        var deleted = await f.Host.Services.GetRequiredService<Atlas.Server.Platform.Retention.OperationalRetention>().RunAsync(default);
        Assert.Equal(2, deleted["Notification"] + deleted["NotificationRead"]);
        Assert.Equal(2L, await ScalarAsync(f, f.Reader.Id, """SELECT count(*) FROM "Notification" WHERE "RecipientId"=:id AND "AggregationKey" IN ('b','d')"""));
    }

    /// <summary>A page of the inbox costs the same statements whatever it holds (no authorization per item).</summary>
    [PostgreSqlFact]
    public async Task The_list_costs_a_constant_number_of_statements()
    {
        await using var f = await CommentsFixture.CreateAsync(outbox: true);
        var one = await f.BoardAsync("One");
        await f.PostAsync(f.Writer, one, Mention(f.Reader));
        for (var i = 0; i < 5; i++)
        {
            await f.PostAsync(f.Writer, await f.BoardAsync("Board " + i), Mention(f.Manager));
        }
        await f.PostAsync(f.Writer, await f.PageAsync("Stránka", "Text"), Mention(f.Manager));
        await DrainAsync(f);

        var statistics = f.Host.OpenSession().SessionFactory.Statistics;
        statistics.IsStatisticsEnabled = true;
        statistics.Clear();
        Assert.Single((await InboxAsync(f, f.Reader)).Items);
        var small = statistics.PrepareStatementCount;
        statistics.Clear();
        Assert.Equal(6, (await InboxAsync(f, f.Manager)).Items.Count);
        Assert.Equal(small, statistics.PrepareStatementCount);
    }

    /// <summary>ARCH1-9: the security events go to the account's own channel of /hubs/live, which only the account's connections
    /// subscribe (UserLiveChannel), and to nobody else's.</summary>
    [PostgreSqlFact]
    public async Task Security_events_reach_only_the_accounts_own_live_channel()
    {
        await using var f = await CommentsFixture.CreateAsync();
        var reader = f.Boards.SubscribeTo(f.Reader, UserLiveChannel.Name(f.Reader.Id), false);
        var writer = f.Boards.SubscribeTo(f.Writer, UserLiveChannel.Name(f.Writer.Id), false);
        ISecurityEvents events = new LiveSecurityEvents(f.Host.Services.GetRequiredService<Atlas.Server.Platform.Live.LiveRelay>());
        var secret = Guid.NewGuid();
        var mark = f.Mark();

        await events.SecretAccessRevokedAsync(f.Reader.Id, new SecretAccessRevoked(secret));
        await events.AccessRequestChangedAsync([f.Reader.Id, f.Reader.Id], new AccessRequestChanged(Guid.NewGuid(), secret, Guid.NewGuid(), "approved"));

        Assert.Equal(secret, Assert.Single(f.Since<SecretAccessRevoked>(mark, reader, LiveSecurityEvents.AccessRevoked)).SecretId);
        Assert.Equal("approved", Assert.Single(f.Since<AccessRequestChanged>(mark, reader, LiveSecurityEvents.RequestChanged)).State);
        Assert.Empty(f.Since<object>(mark, writer, LiveSecurityEvents.AccessRevoked));
        Assert.Empty(f.Since<object>(mark, writer, LiveSecurityEvents.RequestChanged));
    }

    private static async Task<string> GrantVersionAsync(CommentsFixture f, Guid item, Guid user)
    {
        using var session = f.Host.OpenSession();
        var activeFrom = await session.CreateSQLQuery("""SELECT "ActiveFrom" FROM "ResourceGrant" WHERE "ObjectId"=:item AND "UserId"=:user AND "ActiveTo" IS NULL""")
            .SetParameter("item", item).SetParameter("user", user).UniqueResultAsync<DateTime>();
        return Atlas.Server.Platform.Validation.AdministrationVersion.Format(DateTime.SpecifyKind(activeFrom, DateTimeKind.Utc));
    }

    internal static string Mention(UserAccount account) => $"[{account.DisplayName}](a-contact:?kind=internal&internalUser.id={account.Id:D})";

    /// <summary>A page whose paragraph mentions the accounts as the editor's account mention does (a contactInline with internalUser).</summary>
    internal static string Rich(params UserAccount[] accounts)
    {
        // An inline contact holds exactly one contact.
        var contacts = string.Join(",", accounts.Select(a => """{"type":"contactInline","attrs":{"data":{"version":1,"contacts":[""" + $$$"""{"name":"{{{a.DisplayName}}}","kind":"internal","internalUser":{"id":"{{{a.Id:D}}}","name":"{{{a.DisplayName}}}"}}""" + "]}}}"));
        return """{"format":"atlas-rich","version":1,"plugins":[{"id":"contacts","version":2}],"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Pro "},"""
            + contacts + "]}]}}";
    }

    internal static Task<NotificationList> InboxAsync(CommentsFixture f, UserAccount account) => f.As<NotificationList>(account, new ListNotifications(null));

    internal static Task DrainAsync(CommentsFixture f) => Atlas.Server.Tests.Events.OutboxTests.DrainAsync(f.Host);

    /// <summary>Through the outbox, as a producer sends it.</summary>
    private static async Task DeliverAsync(CommentsFixture f, NotificationRequested request)
    {
        await f.Host.InvokeAsync(new Atlas.Server.Tests.Events.EnqueueNotification(Guid.NewGuid(), request));
        await DrainAsync(f);
    }

    private static async Task<long> ScalarAsync(CommentsFixture f, Guid id, string sql)
    {
        using var session = f.Host.OpenSession();
        return Convert.ToInt64(await session.CreateSQLQuery(sql).SetParameter("id", id).UniqueResultAsync(), CultureInfo.InvariantCulture);
    }
}
