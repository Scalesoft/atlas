using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Contacts.LinkRun;
using Atlas.Server.Features.Contacts.LinkRun.Commands;
using Atlas.Server.Features.Contacts.LinkRun.Queries;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Objects.Queries;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.Relations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Contacts;

/// <summary>The link run's safety (contacts spec §6, K4), a run of the job contacts.link: wiki contacts only from what every wiki reader reads,
/// a batch that a concurrent save cannot revert, a run that ends (failed, cancelled) instead of blocking every next one, rewrites the backup
/// sees, and the runs of the ContactLinkRun table that migration 2026110450 converted to JobRun.</summary>
// Hosts of their own (RelationsFixture.CreateAsync, not SharedAsync): the link run (contacts.link, one per installation) reports and rewrites
// every page of the wiki and the tests count all contacts and revisions, which on the shared host would rewrite other tests' pages.
public sealed class ContactLinkRunTests
{
    [PostgreSqlFact]
    public async Task A_page_not_every_wiki_reader_reads_makes_a_contact_of_its_space_and_one_with_its_own_access_none()
    {
        await using var f = await RelationsFixture.CreateAsync();
        // The outsider holds no grant: the space is not every reader's, so its page makes a contact of the space (owner 2026-10-30) …
        var inSpace = await f.PageAsync("Interní", inline: [Inline("Petr", "petr@x.cz")]);
        // … and a page with its own access, read by fewer than its space, makes none.
        var own = await f.PageAsync("Tajné", inline: [Inline("Olga", "olga@x.cz")]);
        await OutboxTests.ExecuteAsync(f.Host.ConnectionString, $"""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"='{own.Id}'""");
        var report = await f.As<ContactLinkReport>(f.Admin, new ReportContactLinks());
        Assert.Equal(("email:petr@x.cz", f.SpaceId, inSpace.Id), (Assert.Single(report.Created).IdentityKey, report.Created[0].SpaceId, report.Created[0].PageIds.Single()));
        Assert.Equal(("email:olga@x.cz", "restricted", own.Id), (Assert.Single(report.Restricted!).IdentityKey, report.Restricted![0].Reason, report.Restricted[0].PageIds.Single()));

        await GrantEveryoneAsync(f);
        var jana = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana", "jana@example.com");
        // A never-published working revision and a template stay restricted; a strong match links even there.
        await f.PageAsync("Koncept", publish: false, inline: [Inline("Karel", "karel@x.cz"), Inline("J. N.", "jana@example.com")]);
        var template = await f.PageAsync("Šablona", inline: [Inline("Eva", "eva@x.cz")]);
        await OutboxTests.ExecuteAsync(f.Host.ConnectionString, $"""UPDATE "Page" SET "IsTemplate"=true WHERE "Id"='{template.Id}'""");
        report = await f.As<ContactLinkReport>(f.Admin, new ReportContactLinks());
        Assert.Equal(("email:petr@x.cz", (Guid?)null), (Assert.Single(report.Created).IdentityKey, report.Created[0].SpaceId));
        Assert.Equal(["email:eva@x.cz", "email:karel@x.cz", "email:olga@x.cz"], report.Restricted!.Select(item => item.IdentityKey));
        Assert.Equal((jana, "email:jana@example.com"), (Assert.Single(report.Linked).ContactId!.Value, report.Linked[0].IdentityKey));

        await RunAsync(f);
        // The job's summary holds the numbers the run page shows: linked, created, restricted.
        Assert.Equal("1,1,3", await OutboxTests.ScalarAsync(f.Host.ConnectionString,
            """SELECT ("Summary"->>'linked') || ',' || ("Summary"->>'created') || ',' || ("Summary"->>'restricted') FROM "JobRun" WHERE "JobId"='contacts.link'"""));
        Assert.Equal(2L, await ScalarAsync(f, """SELECT count(*) FROM "ContentResource" WHERE "TypeId"='atlas.contact' AND "TrashedAt" IS NULL"""));
        Assert.Equal(0L, await ScalarAsync(f, $"""SELECT count(*) FROM "PageRevision" WHERE "ContentJson" ~ '{Linked("(Karel|Eva|Olga)")}'"""));
        Assert.Equal(1L, await ScalarAsync(f, $"""SELECT count(*) FROM "PageRevision" WHERE "ContentJson" ~ '{Linked("J. N.")}'"""));
        Assert.Equal(1L, await ScalarAsync(f, $"""SELECT count(*) FROM "PageRevision" WHERE "ContentJson" LIKE '%"objectId":"{jana}"%'"""));
    }

    [PostgreSqlFact]
    public async Task A_contact_of_the_space_is_read_by_the_space_s_readers_only()
    {
        await using var f = await RelationsFixture.CreateAsync();
        await f.PageAsync("Interní", inline: [Inline("Petr", "petr@x.cz")]);
        await RunAsync(f);
        var petr = (Guid)(await OutboxTests.ScalarAsync(f.Host.ConnectionString,
            $"""SELECT "Id" FROM "ContentResource" WHERE "TypeId"='atlas.contact' AND "Scope"='space' AND "SpaceId"='{f.SpaceId}'"""))!;
        Assert.Equal("Petr", (await f.As<ObjectDto>(f.Reader, new GetObject(petr))).Title);
        await BoardsFixture.Refused(404, () => f.As<ObjectDto>(f.Outsider, new GetObject(petr)));
    }

    [PostgreSqlFact]
    public async Task A_batch_waits_for_a_save_holding_the_page_and_links_what_it_saved_and_the_backup_sees_the_rewrite()
    {
        await using var f = await RelationsFixture.CreateAsync();
        await GrantEveryoneAsync(f);
        var page = await f.PageAsync("Lidé", inline: [Inline("Petr", "petr@x.cz")]);
        var run = await f.As<ContactLinkRunView>(f.Admin, new StartContactLinkRun());
        Assert.Equal(1, (await f.As<ContactLinkRound>(f.Admin, new ContinueContactLinkRun(run.Id))).Pages);
        var stamp = await StampAsync(f);

        // A save holds the page FOR UPDATE (PageGate.EditAsync) and rewrites the revision; the batch waits and then links the saved text.
        await using var save = new NpgsqlConnection(f.Host.ConnectionString);
        await save.OpenAsync();
        await using var tx = await save.BeginTransactionAsync();
        await Execute(save, $"""SELECT 1 FROM "Page" WHERE "Id"='{page.Id}' FOR UPDATE""");
        var batch = f.As<ContactLinkRound>(f.Admin, new ContinueContactLinkRun(run.Id));
        await Task.Delay(500);
        Assert.False(batch.IsCompleted);
        await Execute(save, $"""UPDATE "PageRevision" SET "ContentJson"=replace("ContentJson",'"text":"Text"','"text":"Uloženo"') WHERE "PageId"='{page.Id}'""");
        await tx.CommitAsync();
        Assert.Equal(1, (await batch).Pages);
        var json = (string)(await OutboxTests.ScalarAsync(f.Host.ConnectionString, $"""SELECT "ContentJson" FROM "PageRevision" WHERE "PageId"='{page.Id}' AND "ActiveTo" IS NULL"""))!;
        Assert.Contains("Uloženo", json);
        Assert.Contains("\"objectId\"", json);
        // The Git backup's module snapshot (.atlas/modules, what a keyless restore reads) is due and exports the rows whose xmin moved
        // (TableBackup.ChangeStampAsync, ExportRowsAsync): the rewrite in place is in it without a new Page.Version.
        Assert.NotEqual(stamp, await StampAsync(f));
    }

    [PostgreSqlFact]
    public async Task A_run_whose_round_fails_or_whose_admin_is_gone_ends_failed_and_an_admin_cancels_one()
    {
        await using var f = await RelationsFixture.CreateAsync();
        await GrantEveryoneAsync(f);
        await f.PageAsync("Lidé", inline: [Inline("Petr", "petr@x.cz")]);
        var runner = f.Host.Services.GetRequiredService<IJobRunner>();

        // A checkpoint no round can read: the round throws every time and the run ends failed (its attempts are the job's JobRetry).
        var first = await f.As<ContactLinkRunView>(f.Admin, new StartContactLinkRun());
        await OutboxTests.ExecuteAsync(f.Host.ConnectionString, $$"""UPDATE "JobRun" SET "Progress"=jsonb_set("Progress",'{identities}','[1]') WHERE "Id"={{first.Id}}""");
        f.Host.Actor.Current = null;
        await runner.RunToEndAsync(ContactLinkJob.JobId, "Test");
        Assert.Equal(("failed", first.Id), await NewestAsync(f));

        // The admin who started it is gone (no foreign key): failed at once, and a new run can start.
        var second = await f.As<ContactLinkRunView>(f.Admin, new StartContactLinkRun());
        Assert.NotEqual(first.Id, second.Id);
        await OutboxTests.ExecuteAsync(f.Host.ConnectionString, $"""UPDATE "JobRun" SET "RequestedBy"='{Guid.NewGuid()}' WHERE "Id"={second.Id}""");
        f.Host.Actor.Current = null;
        await runner.RunToEndAsync(ContactLinkJob.JobId, "Test");
        Assert.Equal(("failed", second.Id), await NewestAsync(f));

        // Only a WikiAdmin cancels; the cancelled run no longer runs and the next one starts.
        var third = await f.As<ContactLinkRunView>(f.Admin, new StartContactLinkRun());
        await BoardsFixture.Refused(403, () => f.As(f.Writer, new CancelContactLinkRun()));
        var cancelled = (await f.As<ContactLinkRunView?>(f.Admin, new CancelContactLinkRun()))!;
        Assert.Equal((third.Id, "cancelled"), (cancelled.Id, cancelled.State));
        Assert.NotNull(cancelled.FinishedAt);
        Assert.Equal("queued", (await f.As<ContactLinkRunView>(f.Admin, new StartContactLinkRun())).State);
    }

    [PostgreSqlFact]
    public async Task A_stop_request_ends_the_run_before_the_next_round_and_not_at_the_next_heartbeat()
    {
        await using var f = await RelationsFixture.CreateAsync();
        await GrantEveryoneAsync(f);
        await f.PageAsync("Lidé", inline: [Inline("Petr", "petr@x.cz")]);
        // The admin's stop arrives as the first round commits (the heartbeat would read it only after 30 s).
        await OutboxTests.ExecuteAsync(f.Host.ConnectionString, """
            CREATE FUNCTION stop_after_first_round() RETURNS trigger AS $$ BEGIN NEW."CancelRequested" = true; RETURN NEW; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER stop_after_first_round BEFORE UPDATE OF "Progress" ON "JobRun" FOR EACH ROW
              WHEN (jsonb_typeof(NEW."Progress"->'identities') = 'object') EXECUTE FUNCTION stop_after_first_round()
            """);
        await f.As<ContactLinkRunView>(f.Admin, new StartContactLinkRun());
        f.Host.Actor.Current = f.Admin;
        await f.Host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(ContactLinkJob.JobId, "Test");

        // Cancelled without an error, after the first round (the contact it created stays) and before any page was written.
        var run = (await f.As<ContactLinkRunView?>(f.Admin, new GetContactLinkRun()))!;
        Assert.Equal(("cancelled", 0), (run.State, run.PagesDone));
        Assert.Equal("", await OutboxTests.ScalarAsync(f.Host.ConnectionString, $"""SELECT COALESCE("Error", '') FROM "JobRun" WHERE "Id"={run.Id}"""));
        Assert.Equal(1L, await ScalarAsync(f, """SELECT count(*) FROM "ContentResource" WHERE "TypeId"='atlas.contact' AND "TrashedAt" IS NULL"""));
        Assert.Equal(0L, await ScalarAsync(f, """SELECT count(*) FROM "PageRevision" WHERE "ContentJson" LIKE '%"objectId"%'"""));
    }

    [PostgreSqlFact]
    public async Task After_migration_2026103186_an_existing_run_keeps_its_state_and_a_run_can_end_failed_or_cancelled()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_contact_link_run_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026103186);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "ContactLinkRun"("Id","State","StartedById","StartedAt","Pages","Report")
            VALUES ('00000000-0000-0000-0000-000000000001','done',gen_random_uuid(),now(),1,'{}'),('00000000-0000-0000-0000-000000000002','running',gen_random_uuid(),now(),1,'{}')
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        Assert.Equal("done,running", await OutboxTests.ScalarAsync(schema.ConnectionString, """SELECT string_agg("State", ',' ORDER BY "Id") FROM "ContactLinkRun" """));
        await OutboxTests.ExecuteAsync(schema.ConnectionString, """UPDATE "ContactLinkRun" SET "State"='failed' WHERE "State"='running'""");
        await OutboxTests.ExecuteAsync(schema.ConnectionString, """UPDATE "ContactLinkRun" SET "State"='cancelled' WHERE "State"='done'""");
        await Assert.ThrowsAsync<PostgresException>(() => OutboxTests.ExecuteAsync(schema.ConnectionString, """UPDATE "ContactLinkRun" SET "State"='paused'"""));
    }

    [PostgreSqlFact]
    public async Task After_migration_2026110171_the_runs_primary_keys_have_names_and_a_recorded_run_keeps_its_row()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_contact_run_constraints_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110171);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "ContactLinkRun"("Id","State","StartedById","StartedAt","Pages","Report")
            VALUES ('00000000-0000-0000-0000-000000000001','done',gen_random_uuid(),now(),1,'{}')
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);

        // PER-S-8: named like the constraints around them.
        Assert.Equal("PK_ContactLinkRun", await OutboxTests.ScalarAsync(schema.ConnectionString, """
            SELECT string_agg(conname, ',' ORDER BY conname) FROM pg_constraint WHERE conrelid = to_regclass('"ContactLinkRun"') AND contype = 'p'
            """));
        Assert.Equal(1L, await OutboxTests.ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "ContactLinkRun" """));
    }

    [PostgreSqlFact]
    public async Task Migration_2026110450_converts_the_link_runs_to_job_runs_and_the_running_one_goes_on_from_its_checkpoint()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_contact_link_job_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110450);
        const string Admin = "00000000-0000-0000-0000-0000000000a1", Contact = "00000000-0000-0000-0000-0000000000c1", Cursor = "00000000-0000-0000-0000-0000000000f1";
        // As 3.2.2 left them: a done run (its report from before restricted existed), a failed and a cancelled one without a cursor, and the one
        // running after its first round but before the first committed batch (identities decided, no cursor yet).
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $$"""
            INSERT INTO "ContactLinkRun"("Id","State","StartedById","StartedAt","FinishedAt","Pages","PagesDone","Revisions","AfterPageId","Identities","Report") VALUES
              ('00000000-0000-0000-0000-000000000001','done','{{Admin}}','2026-10-01 10:00+00','2026-10-01 10:05+00',4,4,6,'{{Cursor}}','{"email:a@x.cz":"{{Contact}}"}',
               '{"mode":"apply","linked":[{"name":"A","identityKey":"email:a@x.cz","occurrences":2,"pageIds":[]}],"created":[],"unclear":[{"name":"B","identityKey":"name:b","occurrences":1,"pageIds":[]}],"pages":4,"revisions":6,"restricted":null}'),
              ('00000000-0000-0000-0000-000000000002','failed','{{Admin}}','2026-10-02 10:00+00','2026-10-02 10:01+00',3,1,0,NULL,NULL,
               '{"mode":"apply","linked":[],"created":[],"unclear":[],"pages":3,"revisions":3,"restricted":[]}'),
              ('00000000-0000-0000-0000-000000000003','cancelled','{{Admin}}','2026-10-03 10:00+00',NULL,3,0,0,NULL,NULL,
               '{"mode":"apply","linked":[],"created":[],"unclear":[],"pages":3,"revisions":3,"restricted":[]}'),
              ('00000000-0000-0000-0000-000000000004','running','{{Admin}}','2026-10-04 10:00+00',NULL,9,3,2,NULL,'{"email:a@x.cz":"{{Contact}}"}',
               '{"mode":"apply","linked":[{"name":"A","identityKey":"email:a@x.cz","occurrences":2,"pageIds":[]}],"created":[],"unclear":[],"pages":9,"revisions":9,"restricted":[{"name":"C","identityKey":"email:c@x.cz","occurrences":1,"pageIds":[],"reason":"restricted"}]}')
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);

        // Every run is a run of the job, in the order it began and for the admin who started it; done is succeeded, the running one is queued
        // again; the ContactLinkRun rows are untouched.
        Assert.Equal("succeeded,failed,cancelled,queued", await OutboxTests.ScalarAsync(host.ConnectionString,
            """SELECT string_agg("State", ',' ORDER BY "QueuedAt") FROM "JobRun" WHERE "JobId"='contacts.link'"""));
        Assert.Equal("4,4,4", await OutboxTests.ScalarAsync(host.ConnectionString, """
            SELECT count(*) || ',' || count(*) FILTER (WHERE "RequestedBy"='00000000-0000-0000-0000-0000000000a1') || ',' || (SELECT count(*) FROM "ContactLinkRun")
            FROM "JobRun" WHERE "JobId"='contacts.link'
            """));
        // Finished runs are finished (a cancelled one without an end time ends at its start) and the failed one says why; the queued one waits for its first attempt.
        Assert.Equal("1,1,1,0,1", await OutboxTests.ScalarAsync(host.ConnectionString, """
            SELECT count(*) FILTER (WHERE "FinishedAt" IS NOT NULL AND "State"='succeeded') || ',' || count(*) FILTER (WHERE "Error" IS NOT NULL AND "State"='failed') || ',' ||
              count(*) FILTER (WHERE "State"='cancelled' AND "FinishedAt" IS NOT NULL) || ',' || count(*) FILTER (WHERE "State"='queued' AND "FinishedAt" IS NOT NULL) || ',' ||
              count(*) FILTER (WHERE "State"='queued' AND "StartedAt" IS NULL AND "Attempt"=0) FROM "JobRun" WHERE "JobId"='contacts.link'
            """));
        // The numbers of the run page; a report without restricted counts none.
        Assert.Equal("4,4,6,1,0,1,0", await OutboxTests.ScalarAsync(host.ConnectionString, """
            SELECT concat_ws(',', "Summary"->>'pages', "Summary"->>'pagesDone', "Summary"->>'revisions', "Summary"->>'linked', "Summary"->>'created', "Summary"->>'unclear', "Summary"->>'restricted')
            FROM "JobRun" WHERE "State"='succeeded' AND "JobId"='contacts.link'
            """));

        // A cursor stays and a missing one becomes the empty Guid the job starts from, where the checkpoint is read as ContactLinkProgress
        // (a retried failed run and the queued one read it so).
        Assert.Equal($"{Cursor},{Guid.Empty},{Guid.Empty},{Guid.Empty}", await OutboxTests.ScalarAsync(host.ConnectionString,
            """SELECT string_agg("Progress"->>'afterPageId', ',' ORDER BY "QueuedAt") FROM "JobRun" WHERE "JobId"='contacts.link'"""));
        var checkpoints = ((string)(await OutboxTests.ScalarAsync(host.ConnectionString,
            """SELECT string_agg("Progress"::text, E'\n' ORDER BY "QueuedAt") FROM "JobRun" WHERE "JobId"='contacts.link'"""))!).Split('\n');
        Assert.All(checkpoints, json => Assert.NotNull(JsonSerializer.Deserialize<ContactLinkProgress>(json, ContactLinkRuns.Json)));

        // The queued run goes on from its checkpoint: the identities its first round decided, the report and the position the page shows,
        // and its round reads the checkpoint without a cursor (no page, so the run is done).
        Assert.Equal(Contact, await OutboxTests.ScalarAsync(host.ConnectionString,
            """SELECT "Progress"->'identities'->>'email:a@x.cz' FROM "JobRun" WHERE "State"='queued' AND "JobId"='contacts.link'"""));
        host.Actor.Current = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var run = (await host.InvokeAsync<ContactLinkRunView?>(new GetContactLinkRun()))!;
        Assert.Equal(("queued", 9, 3, 2, 1), (run.State, run.Pages, run.PagesDone, run.Revisions, run.Report.Restricted!.Count));
        Assert.Equal(0, (await host.InvokeAsync<ContactLinkRound>(new ContinueContactLinkRun(run.Id))).Pages);
    }

    [PostgreSqlFact]
    public async Task Migration_2026110340_drops_the_split_run_of_2026103197_and_a_running_one_leaves_nothing_behind()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_contact_split_drop_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110340);
        // As 3.2.1 left an installation whose split still waited on a page: running, deferred, with a next pass.
        await OutboxTests.ExecuteAsync(schema.ConnectionString, """
            UPDATE "ContactSplitRun" SET "PagesDone"=3, "Revisions"=2, "Deferred"=1, "NextPassAt"=now() + interval '10 minutes'
            """);
        Assert.Equal(1L, await OutboxTests.ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "ContactSplitRun" WHERE "State"='running'"""));
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        Assert.Equal(0L, await OutboxTests.ScalarAsync(host.ConnectionString, """SELECT count(*) FROM information_schema.tables WHERE table_name = 'ContactSplitRun'"""));
    }

    [PostgreSqlFact]
    public async Task The_runs_plan_reads_the_pages_and_creates_the_contacts_in_checkpointed_batches_and_resumes_to_the_reports_result()
    {
        await using var f = await RelationsFixture.CreateAsync();
        await GrantEveryoneAsync(f);
        // More pages and people than one batch; one person written differently on the first and the last page, in different batches.
        const int Pages = ContactLinkMigration.BatchPages + 10;
        static Guid PageId(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
        string Content(int n) => """{"format":"atlas-rich","version":1,"plugins":[{"id":"pages","version":1},{"id":"contacts","version":2}],"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Za provoz "},"""
            + Inline($"Osoba {n}", $"osoba{n}@example.cz") + (n is 1 or Pages ? "," + Inline(n == 1 ? "Spor Jedna" : "Spor Dva", "spor@example.cz") : "") + "]}]}}";
        var owner = f.Writer.Id;
        await OutboxTests.ExecuteAsync(f.Host.ConnectionString, $"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES {string.Join(",", Enumerable.Range(1, Pages).Select(n => $"('{PageId(n)}','{f.SpaceId}','{owner}',1)"))};
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel") VALUES
              {string.Join(",", Enumerable.Range(1, Pages).Select(n => $"(gen_random_uuid(),'{PageId(n)}',1,'Stránka {n}','{Content(n)}','Text','{owner}',now(),'web')"))};
            UPDATE "Page" p SET "PublishedRevisionId"=r."Id" FROM "PageRevision" r WHERE r."PageId"=p."Id" AND p."Id" BETWEEN '{PageId(1)}' AND '{PageId(Pages)}';
            """);
        var expected = await f.As<ContactLinkReport>(f.Admin, new ReportContactLinks());
        Assert.Equal((Pages, Pages, Pages), (expected.Pages, expected.Revisions, expected.Created.Count));
        Assert.Equal(("email:spor@example.cz", "inconsistent"), (Assert.Single(expected.Unclear).IdentityKey, expected.Unclear[0].Reason));
        var contacts = await ScalarAsync(f, """SELECT count(*) FROM "ContentResource" WHERE "TypeId"='atlas.contact'""");

        // The start reads no page; the first round reads one batch of pages, checkpoints it and creates nothing yet.
        var run = await f.As<ContactLinkRunView>(f.Admin, new StartContactLinkRun());
        Assert.Equal(0, run.Report.Pages);
        Assert.Equal(1, (await f.As<ContactLinkRound>(f.Admin, new ContinueContactLinkRun(run.Id))).Pages);
        Assert.Equal($"{PageId(ContactLinkMigration.BatchPages)}|false|", await OutboxTests.ScalarAsync(f.Host.ConnectionString,
            $"""SELECT ("Progress"->'preparation'->>'afterPageId') || '|' || ("Progress"->'preparation'->>'collected') || '|' || COALESCE("Progress"->>'identities', '') FROM "JobRun" WHERE "Id"={run.Id}"""));
        Assert.Equal(contacts, await ScalarAsync(f, """SELECT count(*) FROM "ContentResource" WHERE "TypeId"='atlas.contact'"""));
        // The second reads the rest and creates the first batch of people (by identity), each round its own transaction under the directory's lock.
        Assert.Equal(1, (await f.As<ContactLinkRound>(f.Admin, new ContinueContactLinkRun(run.Id))).Pages);
        Assert.Equal(contacts + ContactLinkMigration.BatchPages, await ScalarAsync(f, """SELECT count(*) FROM "ContentResource" WHERE "TypeId"='atlas.contact'"""));
        Assert.Equal(0, (await f.As<ContactLinkRunView?>(f.Admin, new GetContactLinkRun()))!.Report.Pages);

        // The job resumes from the checkpoint to the end: the report's result, every person created once and every page linked.
        f.Host.Actor.Current = f.Admin;
        await f.Host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(ContactLinkJob.JobId, "Test");
        var done = (await f.As<ContactLinkRunView?>(f.Admin, new GetContactLinkRun()))!;
        Assert.Equal(("succeeded", Pages, Pages), (done.State, done.Report.Pages, done.PagesDone));
        static string Items(IEnumerable<ContactLinkItem> items) => string.Join(";", items.Select(item => $"{item.IdentityKey}|{item.Occurrences}|{string.Join(",", item.PageIds)}|{item.Reason}|{item.SpaceId}"));
        Assert.Equal((Items(expected.Linked), Items(expected.Created), Items(expected.Unclear), Items(expected.Restricted!)),
            (Items(done.Report.Linked), Items(done.Report.Created), Items(done.Report.Unclear), Items(done.Report.Restricted!)));
        Assert.Equal(Pages, done.Report.Created.Select(item => item.ContactId!.Value).Distinct().Count());
        Assert.Equal(contacts + Pages, await ScalarAsync(f, """SELECT count(*) FROM "ContentResource" WHERE "TypeId"='atlas.contact'"""));
        var after = await f.As<ContactLinkReport>(f.Admin, new ReportContactLinks());
        Assert.Equal((0, "email:spor@example.cz"), (after.Created.Count, Assert.Single(after.Unclear).IdentityKey));
    }

    /// <summary>Every active account reads the fixture's space, so its published pages are every wiki reader's.</summary>
    internal static Task GrantEveryoneAsync(RelationsFixture f) => OutboxTests.ExecuteAsync(f.Host.ConnectionString, $"""
        INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","Role")
        SELECT gen_random_uuid(), '{f.SpaceId}', a."Id", 1 FROM "UserAccount" a
        WHERE a."IsActive" AND NOT EXISTS (SELECT 1 FROM "SpaceGrant" g WHERE g."SpaceId"='{f.SpaceId}' AND g."UserId"=a."Id" AND g."ActiveTo" IS NULL)
        """);

    /// <summary>Starts a run as the admin and runs it to its end as the job runner does. The job acts as the admin through ActingAccount, which
    /// the test host's actor does not read: the admin is the test's actor for the run.</summary>
    private static async Task RunAsync(RelationsFixture f)
    {
        await f.As<ContactLinkRunView>(f.Admin, new StartContactLinkRun());
        f.Host.Actor.Current = f.Admin;
        await f.Host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(ContactLinkJob.JobId, "Test");
        var (state, id) = await NewestAsync(f);
        Assert.True(state == "succeeded", $"{state}: {await OutboxTests.ScalarAsync(f.Host.ConnectionString, $"""SELECT "Error" FROM "JobRun" WHERE "Id"={id}""")}");
    }

    private static async Task<(string State, long Id)> NewestAsync(RelationsFixture f)
    {
        var run = (await f.As<ContactLinkRunView?>(f.Admin, new GetContactLinkRun()))!;
        return (run.State, run.Id);
    }

    private static string Inline(string name, string email) => new JsonObject
    {
        ["type"] = "contactInline",
        ["attrs"] = new JsonObject { ["data"] = new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(new JsonObject { ["name"] = name, ["emails"] = new JsonArray(email) }) } }
    }.ToJsonString();

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A contact object of the name with an objectId, as a PostgreSQL regular expression.</summary>
    private static string Linked(string name) => $$"""\{[^{}]*"name":"{{name}}"[^{}]*"objectId"|"objectId"[^{}]*"name":"{{name}}"[^{}]*\}""";

    private static async Task<object?> StampAsync(RelationsFixture f) => await OutboxTests.ScalarAsync(f.Host.ConnectionString,
        """SELECT count(*) || ':' || sum(CAST(CAST(xmin AS text) AS bigint)) FROM "PageRevision" """);

    private static async Task<long> ScalarAsync(RelationsFixture f, string sql) => (long)(await OutboxTests.ScalarAsync(f.Host.ConnectionString, sql))!;
}
