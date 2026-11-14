using System.Collections.Concurrent;
using Atlas.Server.Features.Boards;
using Atlas.Server.Features.Boards.Commands;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Entities;
using Atlas.Server.Features.Notifications;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Events.Commands;
using Atlas.Server.Platform.Events.Queries;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Linking;
using Atlas.Server.Platform.Retention;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace Atlas.Server.Tests.Events;

/// <summary>The durable outbox on Wolverine's queue over a real database: sent with the transaction, delivered after it, at least once,
/// to each consumer on its own.</summary>
public sealed class OutboxTests
{
    [PostgreSqlFact]
    public async Task A_rollback_sends_nothing_a_commit_delivers_the_message_and_a_test_host_without_the_queue_sends_nothing()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_outbox_write_", outbox: true);
        var rolledBack = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.InvokeAsync(new EnqueueProbe(rolledBack, null, 0, Fail: true)));
        Assert.Equal(0L, await ScalarAsync(host, $"""SELECT count(*) FROM wolverine_incoming_envelopes WHERE message_type LIKE '%{nameof(OutboxProbe)}'"""));

        var committed = Guid.NewGuid();
        var tracked = await host.TrackAsync(() => host.InvokeAsync(new EnqueueProbe(committed, "k", 0, Fail: false)));
        var envelope = Assert.Single(tracked.Executed.Envelopes(), item => item.Message is OutboxProbe);
        Assert.Equal("k", envelope.GroupId);
        Assert.Equal([envelope.Id], OutboxProbeHandler.Ids(committed));
        Assert.Empty(OutboxProbeHandler.Ids(rolledBack));
        await DrainAsync(host);
        Assert.Equal("Handled", await ScalarAsync(host, $"""SELECT status FROM wolverine_incoming_envelopes WHERE id = '{envelope.Id}' AND received_at = '{Outbox.QueueUri}'"""));

        await using var plain = await AtlasTestHost.CreateAsync("atlas_outbox_plain_");
        var unsent = Guid.NewGuid();
        await plain.InvokeAsync(new EnqueueProbe(unsent, null, 0, Fail: false));
        Assert.Empty(OutboxProbeHandler.Ids(unsent));

        Assert.Equal("boards", Outbox.ModuleOf(typeof(ApplyBoardBatch)));
        Assert.Equal("pages", Outbox.ModuleOf(typeof(SavePage)));
        Assert.Equal("platform", Outbox.ModuleOf(typeof(ContentRevised)));
    }

    /// <summary>A delivery whose handler committed but whose envelope was not marked handled (a crash) comes again with the same id
    /// when Wolverine recovers it; the inbox counts its request once (LastMessageId, migration 2026110421).</summary>
    [PostgreSqlFact]
    public async Task A_recovered_envelope_is_delivered_again_with_its_id_and_the_inbox_counts_it_once()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_outbox_recover_", outbox: true);
        var (writer, mentioned) = (await host.SeedUserAsync("writer"), await host.SeedUserAsync("mentioned"));
        var key = Guid.NewGuid();
        var subject = Guid.NewGuid();
        await host.TrackAsync(() => host.InvokeAsync(new EnqueueNotification(key, new NotificationRequested(NotificationKinds.CommentMention, subject,
            writer.Id, [mentioned.Id]))));
        Assert.Single(OutboxProbeHandler.Ids(key));

        await RecoverAsync(host);

        var ids = OutboxProbeHandler.Ids(key);
        Assert.Equal(2, ids.Length);
        Assert.Equal(ids[0], ids[1]);
        Assert.Equal(1, await ScalarAsync(host, $"""SELECT "EventCount" FROM "Notification" WHERE "SubjectId" = '{subject}'"""));
    }

    [PostgreSqlFact]
    public async Task A_failing_handler_is_retried_with_the_same_id_then_dead_lettered_and_purged_after_the_retention()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_outbox_retry_", outbox: true);
        var once = Guid.NewGuid();
        await host.InvokeAsync(new EnqueueProbe(once, null, 1, Fail: false));
        await DrainAsync(host);
        var ids = OutboxProbeHandler.Ids(once);
        Assert.Equal(2, ids.Length);
        Assert.Equal(ids[0], ids[1]);
        Assert.Equal(0L, await DeadAsync(host));

        var never = Guid.NewGuid();
        await host.InvokeAsync(new EnqueueProbe(never, null, int.MaxValue, Fail: false));
        await DrainAsync(host);
        Assert.Equal(Outbox.MaxAttempts, OutboxProbeHandler.Ids(never).Length);
        Assert.Equal(1L, await DeadAsync(host));
        Assert.Contains("probe failure", (string?)await ScalarAsync(host, $"""SELECT exception_message FROM wolverine_dead_letters WHERE received_at = '{Outbox.QueueUri}'"""));

        var retention = host.Services.GetRequiredService<OperationalRetention>();
        Assert.Equal(0, (await retention.RunAsync(default))["OutboxDead"]);
        await ExecuteAsync(host, """UPDATE wolverine_dead_letters SET failed_at = now() - interval '31 days'""");
        Assert.Equal(1, (await retention.RunAsync(default))["OutboxDead"]);
        Assert.Equal(0L, await DeadAsync(host));
    }

    [PostgreSqlFact]
    public async Task A_wiki_administrator_sees_dead_letters_and_retries_them_with_fresh_attempts()
    {
        await using var f = await BoardsFixture.CreateAsync(outbox: true);
        var host = f.Host;
        var key = Guid.NewGuid();
        await host.InvokeAsync(new EnqueueProbe(key, null, Outbox.MaxAttempts, Fail: false));
        await DrainAsync(host);
        var health = await f.As<OutboxHealth>(f.Admin, new GetOutboxHealth());
        Assert.Equal((0, 1), (health.Pending, health.Dead));
        Assert.Contains("probe failure", health.LastError);
        Assert.DoesNotContain("\n", health.LastError);
        Assert.NotNull(health.LastFailedAt);
        await BoardsFixture.Refused(403, () => f.As<OutboxHealth>(f.Writer, new GetOutboxHealth()));
        await BoardsFixture.Refused(403, () => f.As<RetriedOutboxMessages>(f.Writer, new RetryDeadOutboxMessages()));

        Assert.Equal(1, (await f.As<RetriedOutboxMessages>(f.Admin, new RetryDeadOutboxMessages())).Retried);
        Assert.Equal((1, 0), await f.As<OutboxHealth>(f.Admin, new GetOutboxHealth()) is var retried ? (retried.Pending, retried.Dead) : default);
        // The ninth delivery succeeds (the probe fails its first eight).
        await DrainAsync(host);
        Assert.Equal(Outbox.MaxAttempts + 1, OutboxProbeHandler.Ids(key).Length);
        Assert.Equal((0, 0), await f.As<OutboxHealth>(f.Admin, new GetOutboxHealth()) is var after ? (after.Pending, after.Dead) : default);
    }

    /// <summary>ARCH1-3: every consumer that takes an event gets its own delivery with its own attempts and dead letter; one failing never
    /// repeats or blocks another. A consumer sees only what it takes, and one without a handler (a disabled module) is sent nothing.</summary>
    [PostgreSqlFact]
    public async Task Each_consumer_that_takes_an_event_is_delivered_on_its_own_with_its_own_attempts_and_dead_letter()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_outbox_consumers_", outbox: true);
        var resource = Guid.NewGuid();
        FailingRevisionProbeHandler.Failing.TryAdd(resource, 0);
        var tracked = await host.TrackAsync(() => host.InvokeAsync(new EnqueueRevisions(resource, ["working"])));
        Assert.Equal([nameof(ContentReferenceRevision), nameof(ContentRevisedProbe), nameof(EntityCatalogRevision), nameof(FailingRevisionProbe),
                nameof(Atlas.Server.Features.Collaboration.LiveSeedRevision), nameof(Atlas.Server.Features.Contacts.PageObjectMentionRevision), nameof(Atlas.Server.Features.PagePdfExports.PagePdfExportRevision)],
            tracked.Sent.MessagesOf<IOutboxMessage>().Select(message => message.GetType().Name).Distinct().Order(StringComparer.Ordinal));
        await DrainAsync(host);

        Assert.Single(ContentRevisedProbeHandler.Of(resource));
        Assert.Equal(Outbox.MaxAttempts, FailingRevisionProbeHandler.Attempts(resource));
        Assert.Equal(nameof(FailingRevisionProbe), (string?)await ScalarAsync(host,
            $"""SELECT substring(message_type from '[^.]+$') FROM wolverine_dead_letters WHERE received_at = '{Outbox.QueueUri}'"""));

        // The failing consumer's dead letter does not keep the next event of the resource from the healthy consumers.
        tracked = await host.TrackAsync(() => host.InvokeAsync(new EnqueueRevisions(resource, ["published"])));
        await DrainAsync(host);
        Assert.Equal(["working", "published"], ContentRevisedProbeHandler.Of(resource).Select(message => message.RevisionKind));
        Assert.Contains(tracked.Sent.MessagesOf<PageMentionRevision>(), delivery => delivery.Event.ResourceId == resource);
        Assert.Empty(tracked.Sent.MessagesOf<UnhandledRevisionProbe>());
    }

    /// <summary>One key's messages share a partition of the queue: a burst runs one at a time, in order, to every consumer (no consumer
    /// is spared the older ones any more, as the Atlas outbox's coalescing did); another key runs beside it.</summary>
    [PostgreSqlFact]
    public async Task A_burst_of_one_key_reaches_every_consumer_in_order()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_outbox_burst_", outbox: true);
        var (busy, other) = (Guid.NewGuid(), Guid.NewGuid());
        await host.InvokeAsync(new EnqueueRevisions(busy, ["working", "working", "published", "working", "working"]));
        await host.InvokeAsync(new EnqueueRevisions(other, ["working"]));
        await DrainAsync(host);

        Assert.Equal(["working", "working", "published", "working", "working"], ContentRevisedProbeHandler.Of(busy).Select(message => message.RevisionKind));
        Assert.Equal([1, 2, 3, 4, 5], ContentRevisedProbeHandler.Of(busy).Select(message => (int)message.RevisionId!.Value.ToByteArray()[0]));
        Assert.Single(ContentRevisedProbeHandler.Of(other));
    }

    /// <summary>The ordering rule on Wolverine: one key at a time in the order sent, but a message that failed waits apart for its retry
    /// and lets the later ones of its key pass (the Atlas outbox held them back); the consumers rebuild from the current state.</summary>
    [PostgreSqlFact]
    public async Task One_ordering_key_runs_in_order_and_a_retry_lets_the_later_ones_pass()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_outbox_order_", outbox: true);
        var (older, newer) = (Guid.NewGuid(), Guid.NewGuid());
        await host.InvokeAsync(new EnqueueProbes([new(older, "resource", 1, Fail: false), new(newer, "resource", 0, Fail: false)]));
        await DrainAsync(host);

        Assert.Equal(2, OutboxProbeHandler.Ids(older).Length);
        Assert.Single(OutboxProbeHandler.Ids(newer));
        Assert.True(OutboxProbeHandler.First(older) < OutboxProbeHandler.First(newer));
        Assert.True(OutboxProbeHandler.Last(newer) < OutboxProbeHandler.Last(older));
    }

    /// <summary>Since migration 2026110420 the handlers announce (ContentRevisions), not a trigger: a page's working revisions, its
    /// publication, trash and restore, in order; RevisionPublicationCoverageTests keeps every other writer in line.</summary>
    [PostgreSqlFact]
    public async Task Pages_announce_ContentRevised_from_the_handlers_that_write_them_in_order_and_board_batches_announce_nothing()
    {
        await using var f = await BoardsFixture.CreateAsync(outbox: true);
        var host = f.Host;
        host.Actor.Current = await host.SeedUserAsync("wikiadmin", isWikiAdmin: true);
        var space = await host.SeedSpaceAsync("Provoz");
        const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Plán"}]}]}}""";
        var page = (await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(space, null, "Plán", Rich, null)))).Id;
        await host.InvokeAsync<PageDto>(new PublishPage(page, new PublishPageRequest(await VersionAsync(host, page))));
        await host.InvokeAsync(new RenamePage(page, new RenamePageRequest("Plán provozu", await VersionAsync(host, page))));
        await host.InvokeAsync(new TrashPage(page, new TrashPageRequest(false, await TreeVersionAsync(host, space), await VersionAsync(host, page))));
        await host.InvokeAsync(new UntrashPage(page, new UntrashPageRequest(null, false, await TreeVersionAsync(host, space))));
        // Bare SQL announces nothing: it requests the catalog's rebuild job instead.
        await ExecuteAsync(host, $"""UPDATE "Page" SET "PublishedRevisionId" = NULL WHERE "Id" = '{page}'""");

        await DrainAsync(host);
        var first = (Guid)(await ScalarAsync(host, $"""SELECT "Id" FROM "PageRevision" WHERE "PageId" = '{page}' AND "Version" = 1"""))!;
        var renamed = (Guid)(await ScalarAsync(host, $"""SELECT "Id" FROM "PageRevision" WHERE "PageId" = '{page}' AND "Version" = 2"""))!;
        Assert.Equal(
            [
                new ContentRevised(page, "atlas.page", "working", first), new ContentRevised(page, "atlas.page", "published", first),
                new ContentRevised(page, "atlas.page", "working", renamed), new ContentRevised(page, "atlas.page", "published", first),
                new ContentRevised(page, "atlas.page", "published", first),
            ],
            ContentRevisedProbeHandler.Of(page));

        var board = await f.CreateBoardAsync();
        // ARCH1-4: no consumer reads a board's revision, so its batches send nothing.
        var tracked = await host.TrackAsync(() => f.As<BoardBatchResult>(f.Writer, new ApplyBoardBatch(board, Guid.NewGuid(), [BoardsFixture.Note(Guid.NewGuid(), "once")])));
        Assert.Empty(tracked.Sent.MessagesOf<IOutboxMessage>());
    }

    [PostgreSqlFact]
    public async Task The_migration_leaves_existing_pages_unannounced_and_announces_their_next_revision()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_outbox_upgrade_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026102922);
        const string Owner = "00000000-0000-0000-0000-000000000001";
        const string Page = "00000000-0000-0000-0000-0000000000a1";
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Page}','00000000-0000-0000-0000-00000000000a','{Owner}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              VALUES(gen_random_uuid(),'{Page}',1,'Old','{"{"}{"}"}','old','{Owner}',now(),'web');
            """);

        // Up to the migration that drops the triggers this one adds (2026110420, tested below).
        await ApplyBeforeAsync(schema.ConnectionString, 2026110420, from: 2026102922);

        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "OutboxMessage" """));
        await ExecuteAsync(schema.ConnectionString, $"""
            UPDATE "PageRevision" SET "ActiveTo" = now() WHERE "PageId" = '{Page}';
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              VALUES('00000000-0000-0000-0000-0000000000b2','{Page}',2,'New','{"{"}{"}"}','new','{Owner}',now(),'web');
            """);
        Assert.Equal($$"""{"resourceId": "{{Page}}", "revisionId": "00000000-0000-0000-0000-0000000000b2", "contentType": "atlas.page", "revisionKind": "working"}""",
            await ScalarAsync(schema.ConnectionString, """SELECT "Payload"::text FROM "OutboxMessage" """));
    }

    /// <summary>Migration 2026110420 over an installation whose triggers queued messages: they stay; afterwards bare SQL that writes a
    /// revision announces nothing and requests the catalog's rebuild job instead, as EntityCatalogRebuildJob says, a second request
    /// merging into the queued one. (What happens to the queued messages next is 2026110421's, below.)</summary>
    [PostgreSqlFact]
    public async Task After_migration_2026110420_the_queued_messages_stay_and_bare_SQL_requests_the_rebuild_job()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_revision_triggers_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110420);
        const string Owner = "00000000-0000-0000-0000-000000000001", Page = "00000000-0000-0000-0000-0000000000a1";
        const string Contact = "00000000-0000-0000-0000-0000000000c1";
        await ExecuteAsync(schema.ConnectionString, $$"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{{Owner}}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{{Page}}','00000000-0000-0000-0000-00000000000a','{{Owner}}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              VALUES('00000000-0000-0000-0000-0000000000b1','{{Page}}',1,'Old','{}','old','{{Owner}}',now(),'web');
            INSERT INTO "ContentResource"("Id","TypeId","Scope","CreatedAt","Title","Data")
              VALUES('{{Contact}}','atlas.contact','wiki',now(),'Jana','{"name":"Jana","emails":["jana@example.com"]}');
            """);

        await ApplyBeforeAsync(schema.ConnectionString, 2026110421, from: 2026110420);

        Assert.Equal($"{Page}:working,{Contact}:published", await ScalarAsync(schema.ConnectionString,
            """SELECT string_agg(("Payload"->>'resourceId') || ':' || ("Payload"->>'revisionKind'), ',' ORDER BY "Id") FROM "OutboxMessage" """));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString,
            """SELECT count(*) FROM pg_proc WHERE proname IN ('atlas_outbox_page_revised', 'atlas_outbox_object_revised')"""));

        await ExecuteAsync(schema.ConnectionString, $$"""
            UPDATE "PageRevision" SET "ActiveTo" = now() WHERE "PageId" = '{{Page}}';
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              VALUES('00000000-0000-0000-0000-0000000000b2','{{Page}}',2,'New','{}','new','{{Owner}}',now(),'web');
            UPDATE "Page" SET "PublishedRevisionId" = '00000000-0000-0000-0000-0000000000b2' WHERE "Id" = '{{Page}}';
            UPDATE "ContentResource" SET "Data" = '{"name":"Jana","emails":["jana@example.org"]}' WHERE "Id" = '{{Contact}}';
            INSERT INTO "JobRun"("JobId","Trigger","Reason","State") VALUES ('{{EntityCatalogRebuildJob.JobId}}','event','Migrace','queued') ON CONFLICT DO NOTHING;
            INSERT INTO "JobRun"("JobId","Trigger","Reason","State") VALUES ('{{EntityCatalogRebuildJob.JobId}}','event','Migrace','queued') ON CONFLICT DO NOTHING;
            """);
        Assert.Equal(2L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "OutboxMessage" """));
        Assert.Equal(1L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "JobRun" WHERE "State" = 'queued'"""));
    }

    /// <summary>Migration 2026110421 over the Atlas outbox's undelivered rows (an event, a consumer's delivery, a dead inbox request) and
    /// an inbox item with an outbox id: the table and the catalog's queueing function go, the rows become one queued run of the upgrade
    /// job, the item's id is cleared. The run sends them through Wolverine to today's consumers, and the catalog's rebuild lists its
    /// resources from the new function.</summary>
    [PostgreSqlFact]
    public async Task Migration_2026110421_hands_the_undelivered_rows_to_the_upgrade_job_which_delivers_them_through_Wolverine()
    {
        var schema = await DisposableSchema.CreateAsync("atlas_outbox_wolverine_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110421);
        const string Owner = "00000000-0000-0000-0000-000000000001", Mentioned = "00000000-0000-0000-0000-000000000002";
        const string Contact = "00000000-0000-0000-0000-0000000000c1", Other = "00000000-0000-0000-0000-0000000000c2";
        const string Subject = "00000000-0000-0000-0000-0000000000d1";
        var request = System.Text.Json.JsonSerializer.Serialize(new NotificationRequested(NotificationKinds.CommentMention, Guid.Parse(Subject), Guid.Parse(Owner),
            [Guid.Parse(Mentioned)]), System.Text.Json.JsonSerializerOptions.Web);
        var contact = new ContentRevised(Guid.Parse(Contact), "atlas.contact", "published", null);
        var revised = System.Text.Json.JsonSerializer.Serialize(contact, System.Text.Json.JsonSerializerOptions.Web);
        var delivery = System.Text.Json.JsonSerializer.Serialize(new EntityCatalogRevision(contact with { ResourceId = Guid.Parse(Other) }),
            System.Text.Json.JsonSerializerOptions.Web);
        await ExecuteAsync(schema.ConnectionString, $$"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES
              ('{{Owner}}','writer','Writer',false,true,'stamp'), ('{{Mentioned}}','mentioned','Mentioned',false,true,'stamp');
            INSERT INTO "ContentResource"("Id","TypeId","Scope","CreatedAt","Title","Data") VALUES
              ('{{Contact}}','atlas.contact','wiki',now(),'Jana','{"name":"Jana","emails":["jana@example.com"]}'),
              ('{{Other}}','atlas.contact','wiki',now(),'Petr','{"name":"Petr","emails":["petr@example.com"]}');
            INSERT INTO "Notification"("Id","RecipientId","Kind","SubjectId","AggregationKey","ActorId","EventCount","LastMessageId","CreatedAt","UpdatedAt")
              VALUES (gen_random_uuid(),'{{Mentioned}}','comment.mention','{{Subject}}','comment.mention:{{Subject}}','{{Owner}}',1,41,now(),now());
            INSERT INTO "OutboxMessage"("ModuleId","MessageType","Payload","OrderingKey") VALUES
              ('platform','{{Outbox.TypeName(typeof(ContentRevised))}}','{{revised}}','{{Contact}}'),
              ('platform','{{Outbox.TypeName(typeof(EntityCatalogRevision))}}','{{delivery}}','{{Other}}');
            INSERT INTO "OutboxMessage"("ModuleId","MessageType","Payload","OrderingKey","State","Attempts","LastError") VALUES
              ('platform','{{Outbox.TypeName(typeof(NotificationRequested))}}','{{request}}','inbox:comment.mention:{{Subject}}','dead',8,'boom');
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal(DBNull.Value, await ScalarAsync(schema.ConnectionString, """SELECT to_regclass('"OutboxMessage"')::text"""));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM pg_proc WHERE proname = 'atlas_enqueue_entity_catalog'"""));
        Assert.Equal("uuid", await ScalarAsync(schema.ConnectionString,
            """SELECT data_type FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Notification' AND column_name = 'LastMessageId'"""));
        Assert.Equal(DBNull.Value, await ScalarAsync(schema.ConnectionString, """SELECT "LastMessageId" FROM "Notification" """));
        // The later migrations queue rebuilds of their own (2026110870–872): the upgrade's run is the one with this id.
        Assert.Equal($"{OutboxUpgradeJob.JobId}:3", await ScalarAsync(schema.ConnectionString,
            $"""SELECT "JobId" || ':' || jsonb_array_length("Progress") FROM "JobRun" WHERE "State" = 'queued' AND "JobId" = '{OutboxUpgradeJob.JobId}'"""));
        Assert.Equal($"{Contact},{Other}", await ScalarAsync(schema.ConnectionString,
            """
            SELECT string_agg(r."Id"::text, ',' ORDER BY r."Id") FROM "ContentResource" r
            WHERE r."TypeId" = 'atlas.contact'
               OR r."TypeId" = 'atlas.page' AND EXISTS (SELECT 1 FROM "Page" p JOIN "PageRevision" w ON w."PageId" = p."Id"
                    AND (w."ActiveTo" IS NULL OR w."Id" = p."PublishedRevisionId")
                  WHERE p."Id" = r."Id" AND w."ContentJson" LIKE '%"contact%')
            """));

        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled, outbox: true);
        await DeliverUpgradedAsync(host);
        Assert.Equal("email:jana@example.com,email:petr@example.com", await ScalarAsync(host.ConnectionString,
            $"""SELECT string_agg("IdentityKey", ',' ORDER BY "IdentityKey") FROM "EntityOccurrence" WHERE "ResourceId" IN ('{Contact}', '{Other}')"""));
        Assert.Equal(2, await ScalarAsync(host.ConnectionString, """SELECT "EventCount" FROM "Notification" """));
        Assert.Equal("""{"sent": 3, "dropped": 0}""", await ScalarAsync(host.ConnectionString,
            $"""SELECT "Summary"::text FROM "JobRun" WHERE "JobId" = '{OutboxUpgradeJob.JobId}'"""));
    }

    /// <summary>ARCH3-6: an event with consumers is only ever delivered as their own deliveries, so a handler of the event itself would
    /// never run.</summary>
    [Trait("Category", "Guard")]
    [Fact]
    public void An_event_with_consumers_has_no_handler_of_its_own()
    {
        var server = typeof(Outbox).Assembly;
        var events = server.GetTypes().SelectMany(type => type.GetInterfaces())
            .Where(face => face.IsGenericType && face.GetGenericTypeDefinition() == typeof(IOutboxDelivery<>))
            .Select(face => face.GetGenericArguments()[0]).ToHashSet();
        Assert.Contains(typeof(ContentRevised), events);
        string[] names = ["Handle", "HandleAsync", "Handles", "HandlesAsync", "Consume", "ConsumeAsync", "Consumes", "ConsumesAsync"];
        var direct = server.GetTypes()
            .SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.DeclaredOnly))
            .Where(method => names.Contains(method.Name) && method.GetParameters().Any(parameter => events.Contains(parameter.ParameterType)))
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}").ToArray();
        Assert.True(direct.Length == 0, "Consume an outbox event through its own IOutboxDelivery<T>: " + string.Join(", ", direct));
    }

    /// <summary>PER3-3: migration 2026103020 over an existing queue; the fan-out's look for pending events of its types takes the index
    /// (until 2026110421 drops the table).</summary>
    [PostgreSqlFact]
    public async Task The_migration_indexes_the_pending_rows_by_type_and_the_fan_out_reads_them_through_it()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_outbox_pending_type_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026103020);
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "OutboxMessage"("ModuleId","MessageType","Payload","OrderingKey","State")
            SELECT 'platform', 'Atlas.Server.Features.Entities.EntityCatalogRevision, Atlas.Server', jsonb_build_object('n', n), n::text,
              CASE WHEN n % 100 = 0 THEN 'dead' ELSE 'pending' END
            FROM generate_series(1, 20000) n;
            """);

        await ApplyBeforeAsync(schema.ConnectionString, 2026110421, from: 2026103020);

        Assert.Equal(20000L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "OutboxMessage" """));
        await ExecuteAsync(schema.ConnectionString, """ANALYZE "OutboxMessage" """);
        var plan = (string?)await ScalarAsync(schema.ConnectionString, """
            EXPLAIN (FORMAT JSON) SELECT "Id" FROM "OutboxMessage"
            WHERE "State" = 'pending' AND "MessageType" = ANY(ARRAY['Atlas.Server.Platform.Events.ContentRevised, Atlas.Server'])
            """);
        Assert.Contains("IX_OutboxMessage_PendingType", plan);
        Assert.DoesNotContain("Seq Scan", plan);
    }

    /// <summary>Waits until the outbox's queue holds nothing to run: every envelope handled or a dead letter, the retries of a test
    /// host's short waits included (a durable host).</summary>
    internal static async Task DrainAsync(AtlasTestHost host)
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while ((long)(await ScalarAsync(host.ConnectionString, $"""
                   SELECT (SELECT count(*) FROM wolverine_incoming_envelopes WHERE received_at = '{Outbox.QueueUri}' AND status <> 'Handled'
                     -- A version's pause check (3.6-C) waits half an hour by design; scheduled retries are still waited for.
                     AND NOT (status = 'Scheduled' AND message_type LIKE '%VersionPauseCheck%'))
                     + (SELECT count(*) FROM wolverine_dead_letters WHERE received_at = '{Outbox.QueueUri}' AND replayable)
                   """))! > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The outbox's queue did not drain within a minute.");
            await Task.Delay(20);
        }
    }

    /// <summary>Runs the upgrade job (its run that migration 2026110421 queued, or an empty one) and waits for what it sent.</summary>
    internal static async Task DeliverUpgradedAsync(AtlasTestHost host)
    {
        await host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(OutboxUpgradeJob.JobId, "Aktualizace na verzi 3.3");
        await DrainAsync(host);
    }

    /// <summary>Every envelope the outbox's queue handled is incoming again without an owner, as a crash before Wolverine marked it
    /// handled leaves it: Wolverine recovers and delivers it again with its id.</summary>
    internal static async Task RecoverAsync(AtlasTestHost host)
    {
        await ExecuteAsync(host, $"""
            UPDATE wolverine_incoming_envelopes SET status = 'Incoming', owner_id = 0, keep_until = NULL WHERE received_at = '{Outbox.QueueUri}'
            """);
        await DrainAsync(host);
    }

    /// <summary>Waits until <paramref name="sql"/> gives a positive count: what an in-memory outbox (a site's) delivers.</summary>
    internal static async Task WaitForAsync(string connectionString, string sql)
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (Convert.ToInt64(await ScalarAsync(connectionString, sql)) <= 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Not delivered within a minute: " + sql);
            await Task.Delay(20);
        }
    }

    private static async Task<int> VersionAsync(AtlasTestHost host, Guid page) => (await host.InvokeAsync<PageDto>(new GetPage(page))).Version;

    private static async Task<long> TreeVersionAsync(AtlasTestHost host, Guid space)
        => Convert.ToInt64(await ScalarAsync(host, $"""SELECT "Version" FROM "SpaceOrganization" WHERE "SpaceId" = '{space}'""") ?? 0L);

    private static async Task<long> DeadAsync(AtlasTestHost host)
        => (long)(await ScalarAsync(host, $"""SELECT count(*) FROM wolverine_dead_letters WHERE received_at = '{Outbox.QueueUri}'"""))!;

    private static Task ExecuteAsync(AtlasTestHost host, string sql) => ScalarAsync(host.ConnectionString, sql);

    private static Task<object?> ScalarAsync(AtlasTestHost host, string sql) => ScalarAsync(host.ConnectionString, sql);

    internal static Task ExecuteAsync(string connectionString, string sql) => ScalarAsync(connectionString, sql);

    internal static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    /// <summary>Applies the embedded scripts below a version, with their journal, as the previous release left the schema; with
    /// <paramref name="from"/> only those from it on, over the schema an earlier call left (the test of an old migration stops before a
    /// later one changes what it tests).</summary>
    internal static async Task ApplyBeforeAsync(string connectionString, long version, long from = 0)
    {
        if (from == 0)
        {
            await ExecuteAsync(connectionString, "CREATE TABLE \"SchemaVersion\" (\"Version\" bigint PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
        }
        var assembly = typeof(AtlasDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
                     .Select(name => (Name: name, Version: long.Parse(name.Split('.')[^2].Split('_')[0])))
                     .Where(item => item.Version >= from && item.Version < version).OrderBy(item => item.Version))
        {
            using var stream = assembly.GetManifestResourceStream(resource.Name)!;
            using var reader = new StreamReader(stream);
            await ExecuteAsync(connectionString, await reader.ReadToEndAsync());
            await ExecuteAsync(connectionString, $"INSERT INTO \"SchemaVersion\"(\"Version\") VALUES({resource.Version})");
        }
    }
}

public sealed record EnqueueProbe(Guid Key, string? OrderingKey, int Failures, bool Fail);

/// <summary>Several probes in one transaction, in order.</summary>
public sealed record EnqueueProbes(EnqueueProbe[] Probes);

/// <summary>An inbox request and a probe of <see cref="Key"/> in one transaction.</summary>
public sealed record EnqueueNotification(Guid Key, NotificationRequested Request);

public sealed record OutboxProbe(Guid Key, int Failures) : IOutboxMessage;

/// <summary>Records every delivery (its envelope id and a global tick) and fails the first <see cref="OutboxProbe.Failures"/> of them.</summary>
public static class OutboxProbeHandler
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<(Guid Id, long Tick)>> Deliveries = new();
    private static long s_tick;

    public static void Handle(EnqueueProbe command, Outbox outbox)
    {
        outbox.Enqueue(new OutboxProbe(command.Key, command.Failures), command.OrderingKey);
        if (command.Fail)
        {
            throw new InvalidOperationException("rolled back");
        }
    }

    public static void Handle(EnqueueProbes command, Outbox outbox)
    {
        foreach (var probe in command.Probes)
        {
            outbox.Enqueue(new OutboxProbe(probe.Key, probe.Failures), probe.OrderingKey);
        }
    }

    public static void Handle(EnqueueNotification command, Outbox outbox)
    {
        outbox.Notify(command.Request);
        outbox.Enqueue(new OutboxProbe(command.Key, 0), command.Request.Key);
    }

    public static void Handle(OutboxProbe probe, Envelope envelope)
    {
        var seen = Deliveries.GetOrAdd(probe.Key, _ => new());
        seen.Enqueue((envelope.Id, Interlocked.Increment(ref s_tick)));
        if (seen.Count <= probe.Failures)
        {
            throw new InvalidOperationException($"probe failure {seen.Count}");
        }
    }

    public static Guid[] Ids(Guid key) => Deliveries.TryGetValue(key, out var seen) ? seen.Select(entry => entry.Id).ToArray() : [];

    public static long First(Guid key) => Deliveries[key].First().Tick;

    public static long Last(Guid key) => Deliveries[key].Last().Tick;
}

/// <summary>Enqueues one ContentRevised per kind for each resource, in order; the n-th carries a revision id whose first byte is n.</summary>
public sealed record EnqueueRevisions(Guid[] ResourceIds, string[] Kinds)
{
    public EnqueueRevisions(Guid resourceId, string[] kinds) : this([resourceId], kinds)
    {
    }
}

public static class EnqueueRevisionsHandler
{
    public static void Handle(EnqueueRevisions command, Outbox outbox)
    {
        foreach (var resource in command.ResourceIds)
        {
            for (var index = 0; index < command.Kinds.Length; index++)
            {
                var revision = new byte[16];
                revision[0] = (byte)(index + 1);
                outbox.Enqueue(new ContentRevised(resource, "atlas.page", command.Kinds[index], new Guid(revision)), resource.ToString());
            }
        }
    }
}

/// <summary>A consumer of ContentRevised next to the platform's own: records every delivery of every content type.</summary>
public sealed record ContentRevisedProbe(ContentRevised Event) : IOutboxDelivery<ContentRevised>
{
    public static bool Takes(ContentRevised message) => true;
}

public static class ContentRevisedProbeHandler
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<ContentRevised>> Deliveries = new();

    public static void Handle(ContentRevisedProbe delivery) => Deliveries.GetOrAdd(delivery.Event.ResourceId, _ => new()).Enqueue(delivery.Event);

    public static ContentRevised[] Of(Guid resourceId) => Deliveries.TryGetValue(resourceId, out var seen) ? seen.ToArray() : [];
}

/// <summary>A consumer of pages that fails for the resources a test marks.</summary>
public sealed record FailingRevisionProbe(ContentRevised Event) : IOutboxDelivery<ContentRevised>
{
    public static bool Takes(ContentRevised message) => message.ContentType == "atlas.page";
}

public static class FailingRevisionProbeHandler
{
    public static readonly ConcurrentDictionary<Guid, byte> Failing = new();
    private static readonly ConcurrentDictionary<Guid, int> Counted = new();

    public static int Attempts(Guid resource) => Counted.GetValueOrDefault(resource);

    public static void Handle(FailingRevisionProbe delivery)
    {
        if (Failing.ContainsKey(delivery.Event.ResourceId))
        {
            Counted.AddOrUpdate(delivery.Event.ResourceId, 1, (_, count) => count + 1);
            throw new InvalidOperationException("failing consumer");
        }
    }
}

/// <summary>A consumer without a handler, as one of a disabled module is: the outbox never sends it.</summary>
public sealed record UnhandledRevisionProbe(ContentRevised Event) : IOutboxDelivery<ContentRevised>
{
    public static bool Takes(ContentRevised message) => true;
}
