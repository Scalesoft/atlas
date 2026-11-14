using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Contacts.Commands;
using Atlas.Server.Features.Contacts.Contracts;
using Atlas.Server.Features.Contacts.Queries;
using Atlas.Server.Features.Contacts.LinkRun;
using Atlas.Server.Features.Contacts.LinkRun.Commands;
using Atlas.Server.Features.Contacts.LinkRun.Queries;
using Atlas.Server.Features.Contacts.Persistence;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Queries;
using Atlas.Server.Features.TrashCleanup.Queries;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.Relations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Contacts;

/// <summary>The directory as one set (contacts spec 2026-10-30 §2, §5, §6, K1–K4): one person once across the wiki and the spaces, the link
/// by identity, the merge by redirect and the migration of the contacts written into pages, report first.</summary>
public sealed class ContactDirectoryTests
{
    [PostgreSqlFact]
    public async Task A_person_is_once_across_the_wiki_and_the_spaces_on_creation_and_on_a_change_of_address()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var domain = Domain();
        var jana = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana", $"jana@{domain}");
        var refused = await BoardsFixture.Refused(409, () => f.ContactAsync(f.Writer, ObjectScopes.Space, "Jana N.", $"JANA@{domain}"), "duplicate_contact");
        Assert.Equal(jana, refused.Extensions!["contactId"]);

        var petr = await f.ContactAsync(f.Writer, ObjectScopes.Space, "Petr", $"petr@{domain}");
        await BoardsFixture.Refused(409, () => f.As(f.Writer, Change(petr, $$"""{"emails":["petr@{{domain}}","jana@{{domain}}"]}""")), "duplicate_contact");
        // Its own address again, and another field, are no duplicate.
        await f.As<ObjectChangeResult>(f.Writer, Change(petr, $$"""{"emails":["Petr@{{domain}}"],"title":"PM"}"""));
        // Private contacts stay apart (K1).
        await f.ContactAsync(f.Reader, ObjectScopes.Private, "Jana", $"jana@{domain}");
    }

    [PostgreSqlFact]
    public async Task A_snapshot_links_to_the_directorys_person_reports_differences_and_a_weak_match_creates_with_a_warning()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var (domain, organization, phone, tag) = (Domain(), "Modelsoft " + Tag(), Phone(), Tag());
        var jana = (await f.As<ObjectDto>(f.Writer, new CreateObject(ContactObjectType.Id, ObjectScopes.Wiki, null,
            JsonNode.Parse($$"""{"name":"Jana Nováková","emails":["jana@{{domain}}"],"phones":["{{phone}}"],"organization":"{{organization}}"}""")!.AsObject()))).Id;

        var linked = await f.As<ResolvedContact>(f.Writer, new ResolveContact(Snapshot($$"""{"name":"Jana","emails":["JANA@{{domain}}"],"phones":["602 000 000"],"role":"PM"}""")));
        Assert.Equal((jana, false), (linked.ContactId, linked.Created));
        Assert.Equal(["contact_exists", "contact_conflict", "contact_conflict"], linked.Warnings.Select(warning => warning.Type));
        Assert.Equal(("phones", "name"), (linked.Warnings[1].Field, linked.Warnings[2].Field));
        // The directory holds; the snapshot keeps only its page's role.
        Assert.Equal(("Jana Nováková", phone, "PM", jana.ToString()), (linked.Contact["name"]!.GetValue<string>(), linked.Contact["phones"]![0]!.GetValue<string>(),
            linked.Contact["role"]!.GetValue<string>(), linked.Contact["objectId"]!.GetValue<string>()));

        // The test's own name: another test's „Jana Domů“ would be a weak match too.
        var weak = await f.As<ResolvedContact>(f.Writer, new ResolveContact(Snapshot($$"""{"name":"Jana Domů {{tag}}","phones":["+420 {{phone}}"]}""")));
        Assert.True(weak.Created);
        Assert.Equal(("possible_duplicate", jana), (Assert.Single(weak.Warnings).Type, weak.Warnings[0].ContactId));
        var match = await f.As<ContactMatchResult>(f.Reader, new MatchContact(Snapshot($$"""{"name":"Jana Nováková","organization":"{{organization.ToUpperInvariant()}}"}""")));
        Assert.Null(match.Strong);
        Assert.Equal([jana], match.Weak.Select(candidate => candidate.ContactId));

        // An unresolved name or a template place („PM“) is nobody yet: refused, never created (K1).
        await BoardsFixture.Refused(400, () => f.As(f.Writer, new ResolveContact(Snapshot("""{"name":"PM","role":"PM","unresolved":true}"""))), "contact_unresolved");
        await BoardsFixture.Refused(400, () => f.As(f.Writer, new ResolveContact(Snapshot("""{"name":"Jana Nováková","unresolved":true}"""))), "contact_unresolved");
        Assert.Equal(0L, await ScalarAsync(f, $"""SELECT count(*) FROM "ContentResource" WHERE "TypeId" = '{ContactObjectType.Id}' AND "Title" = 'PM' AND "CreatedBy" = '{f.Writer.Id}'"""));

        // A reader may not create in the wiki directory: refused naming the contact.
        await BoardsFixture.Refused(403, () => f.As(f.Reader, new ResolveContact(Snapshot("""{"name":"Nový"}"""))), "contact_forbidden");
    }

    [PostgreSqlFact]
    public async Task A_merge_redirects_reads_relations_cards_tree_and_usage_to_the_target_and_open_boards_hear_it()
    {
        // A host of its own: DrainAsync waits for the whole outbox envelope table (OutboxTests.DrainAsync).
        await using var f = await RelationsFixture.CreateAsync(outbox: true);
        var jana = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana", "jana@example.com");
        var home = await f.ContactAsync(f.Writer, ObjectScopes.Space, "Jana Domů", "jana@home.example.test");
        var older = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "J. Nováková", "jn@old.example.test");
        var board = await f.BoardAsync("Tým");
        var card = Guid.NewGuid();
        await f.Boards.ApplyAsync(f.Writer, board, RelationsFixture.ObjectCard(card, home));
        var page = await f.PageAsync("Lidé", inline: [Inline("Jana Domů", home)]);
        // A snapshot without objectId matched by the source's address, and a page of a contact merged into the source before.
        var byAddress = await f.PageAsync("Adresy", inline: [Inline("Jana", null, "jana@home.example.test")]);
        var ofOlder = await f.PageAsync("Archiv", inline: [Inline("J. Nováková", older)]);
        await f.As<Atlas.Server.Features.PageOrganization.Contracts.PageTreeItem>(f.Writer,
            new Atlas.Server.Features.TreeReferences.Commands.AddTreeReference(f.SpaceId, new Atlas.Server.Features.TreeReferences.Contracts.AddTreeReferenceRequest(home, ParentId: page.Id)));
        await f.As<MergedContacts>(f.Writer, new MergeContacts(older, home));
        await DrainAsync(f);
        Assert.Equal(5, (await f.As<UsageCount>(f.Writer, new CountObjectUsage(home))).Total);

        await BoardsFixture.Refused(403, () => f.As(f.Reader, new MergeContacts(home, jana)), "forbidden");
        var subscriber = f.Boards.Subscribe(f.Reader, board);
        var sequence = await ScalarAsync(f, $"""SELECT "Sequence" FROM "Board" WHERE "Id"='{board}'""");
        var merged = await f.As<MergedContacts>(f.Writer, new MergeContacts(home, jana));
        Assert.Equal((jana, 1), (merged.ContactId, merged.MovedCards));
        Assert.Equal(["jana@example.com", "jana@home.example.test", "jn@old.example.test"], merged.Contact["emails"]!.AsArray().Select(x => x!.GetValue<string>()));

        var live = (await f.As<LiveContactsResult>(f.Reader, new LiveContacts([home, older]))).Items;
        Assert.Equal([("ok", jana), ("ok", jana)], new[] { live[home], live[older] }.Select(x => (x.State, x.ContactId!.Value)));
        // Every redirect is one hop: the contact merged into the source before follows the target.
        Assert.Equal(1L, await ScalarAsync(f, $"""SELECT count(*) FROM "ContentResource" WHERE "Id"='{older}' AND "Data"->>'mergedInto'='{jana}'"""));
        // The card shows the target as a board batch: logged for a client catching up, sent to the open board at once.
        Assert.Equal(1L, await ScalarAsync(f, $"""SELECT count(*) FROM "BoardItem" WHERE "TargetResourceId"='{jana}' AND "PropsJson"->>'objectId'='{jana}'"""));
        Assert.Equal(sequence + 1, await ScalarAsync(f, $"""SELECT "Sequence" FROM "Board" WHERE "Id"='{board}'"""));
        var sent = Assert.Single(f.Boards.Hub.Sent, x => x.Method == "BoardChanged" && x.Connections.Contains(subscriber));
        var change = Assert.Single(Assert.IsType<Atlas.Server.Features.Boards.Contracts.BoardChangedMessage>(sent.Payload).Changes);
        Assert.Equal(("set", card, jana.ToString()), (change.Type, change.ItemId!.Value, change.Props!["objectId"]!.GetValue<string>()));
        Assert.Equal(1L, await ScalarAsync(f, $"""SELECT count(*) FROM "BoardOperation" WHERE "BoardId"='{board}' AND "ChangesJson" LIKE '%{jana}%'"""));
        // The tree's reference node names the target; the mention rows of the pages are matched again.
        Assert.Equal(1L, await ScalarAsync(f, $"""SELECT count(*) FROM "TreeReference" WHERE "TargetId"='{jana}'"""));
        Assert.Equal(1L, await ScalarAsync(f, $"""SELECT count(DISTINCT "SourcePageId") FROM "PageObjectMention" WHERE "ObjectId"='{jana}'"""));

        // Usage and relations follow the target everywhere: the card, the reference node and three pages (by id, by address, the older merge).
        var usage = await f.As<ObjectUsageList>(f.Writer, new GetObjectUsage(jana));
        Assert.Equal(5, usage.Total);
        Assert.Equal(new[] { board, byAddress.Id, ofOlder.Id, page.Id }.Order(), usage.Items.Where(row => row.Kind != RelationKinds.Reference).Select(row => row.PlaceId).Order());
        Assert.Equal(page.Id, Assert.Single(usage.Items, row => row.Kind == RelationKinds.Reference).PlaceId);
        Assert.Equal(new UsageCount(jana, 5, 5, 0), await f.As<UsageCount>(f.Writer, new CountObjectUsage(jana)));
        Assert.Contains((await f.RelationsAsync(f.Writer, jana, kinds: "mention")).Edges, edge => edge.SourceId == ofOlder.Id && edge.TargetId == jana);
        await BoardsFixture.Refused(404, () => f.As<UsageCount>(f.Writer, new CountObjectUsage(home)));

        // The merged contact's address is the target's now: no new person with it.
        await BoardsFixture.Refused(409, () => f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana", "jana@home.example.test"), "duplicate_contact");
        var trashed = (await f.As<LiveContactsResult>(f.Reader, new LiveContacts([Guid.NewGuid()]))).Items.Values.Single();
        Assert.Equal("missing", trashed.State);
    }

    [PostgreSqlFact]
    public async Task A_merge_whose_list_would_exceed_its_capacity_is_refused_before_any_change_and_one_that_fits_merges_whole()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var domain = Domain();
        var jana = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana", $"jana@{domain}");
        var home = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana Domů", $"home@{domain}");
        static string List(string format, int count) => JsonSerializer.Serialize(Enumerable.Range(1, count).Select(i => string.Format(format, i)));
        var addresses = $"jana{{0}}@{domain}";
        await f.As<ObjectChangeResult>(f.Writer, Change(jana, $$"""{"emails":{{List(addresses, 20)}}}"""));

        var refused = await BoardsFixture.Refused(409, () => f.As(f.Writer, new MergeContacts(home, jana)), "contact_merge_over_capacity");
        Assert.Contains("pole „E-mail“ by mělo 21 hodnot (cílový kontakt 20, ze zdrojového 1 dalších)", refused.Message);
        await f.As<ObjectChangeResult>(f.Writer, Change(jana, $$"""{"emails":{{List(addresses, 19)}},"aliases":{{List("J{0}", 20)}}}"""));
        await f.As<ObjectChangeResult>(f.Writer, Change(home, """{"aliases":["Domácí"]}"""));
        await BoardsFixture.Refused(409, () => f.As(f.Writer, new MergeContacts(home, jana)), "contact_merge_over_capacity");
        Assert.Equal(0L, await ScalarAsync(f, $"""SELECT count(*) FROM "ContentResource" WHERE "Id"='{home}' AND ("TrashedAt" IS NOT NULL OR "Data" ? 'mergedInto')"""));
        Assert.Equal(19L, await ScalarAsync(f, $"""SELECT jsonb_array_length("Data"->'emails')::bigint FROM "ContentResource" WHERE "Id"='{jana}'"""));

        await f.As<ObjectChangeResult>(f.Writer, Change(jana, $$"""{"aliases":{{List("J{0}", 19)}}}"""));
        var merged = await f.As<MergedContacts>(f.Writer, new MergeContacts(home, jana));
        Assert.Equal(20, merged.Contact["emails"]!.AsArray().Count);
        Assert.Equal(20L, await ScalarAsync(f, $"""SELECT jsonb_array_length("Data"->'aliases')::bigint FROM "ContentResource" WHERE "Id"='{jana}'"""));
        Assert.Equal($"home@{domain}", merged.Contact["emails"]![19]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task A_merged_contact_keeps_its_redirect_while_content_names_it_and_a_restore_ends_the_redirect_once_the_target_lets_its_address_go()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var domain = Domain();
        var jana = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana", $"jana@{domain}");
        var home = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana Domů", $"home@{domain}");
        var unused = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "J. Nováková", $"jn@{domain}");
        await f.PageAsync("Lidé", inline: [Inline("Jana Domů", home)]);
        await f.As<MergedContacts>(f.Writer, new MergeContacts(home, jana));
        await f.As<MergedContacts>(f.Writer, new MergeContacts(unused, jana));

        // The page still names the merged contact: its purge would take the page's redirect to the target along.
        await BoardsFixture.Refused(409, () => f.As(f.Admin, new PurgeObject(home)), "contact_merged_in_use");
        Assert.Equal(1L, await ScalarAsync(f, $"""SELECT count(*) FROM "ContentResource" WHERE "Id"='{home}' AND "Data"->>'mergedInto'='{jana}'"""));
        // The cleanup does not choose (nor count as due) the one its purge would refuse.
        using (var scope = f.Host.Services.CreateScope())
        {
            var due = await TrashCleanupCandidates.TrashedAsync(scope.ServiceProvider.GetRequiredService<NHibernate.ISession>(), null, [ContactObjectType.Id],
                DateTime.UtcNow.AddDays(1), CancellationToken.None);
            Assert.Equal([unused], due.Intersect([home, unused]));
        }
        // Nothing names the other one: it goes.
        await f.As(f.Admin, new PurgeObject(unused));
        Assert.Equal(0L, await ScalarAsync(f, $"""SELECT count(*) FROM "ContentResource" WHERE "Id"='{unused}'"""));

        // The target took its address at the merge: restored as it is, the person would be in the directory twice (K1).
        var refused = await BoardsFixture.Refused(409, () => f.As(f.Admin, new RestoreObject(home)), "contact_restore_duplicate");
        Assert.Equal(jana, refused.Extensions!["contactId"]);
        await f.As<ObjectChangeResult>(f.Writer, Change(jana, $$"""{"emails":["jana@{{domain}}"]}"""));
        // Restored, the merged contact is itself again, not the target.
        await f.As<ObjectChangeResult>(f.Admin, new RestoreObject(home));
        Assert.Equal(0L, await ScalarAsync(f, $"""SELECT count(*) FROM "ContentResource" WHERE "Id"='{home}' AND "Data" ? 'mergedInto'"""));
        var live = (await f.As<LiveContactsResult>(f.Reader, new LiveContacts([home]))).Items[home];
        Assert.Equal(("ok", home), (live.State, live.ContactId!.Value));
    }

    [PostgreSqlFact]
    public async Task A_web_save_names_the_merge_target_so_the_merged_contact_goes_once_the_page_is_published()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var domain = Domain();
        var jana = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana", $"jana@{domain}");
        var home = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana Domů", $"home@{domain}");
        var page = await f.PageAsync("Lidé", inline: [Inline("Jana Domů", home)]);
        await f.As<MergedContacts>(f.Writer, new MergeContacts(home, jana));
        await BoardsFixture.Refused(409, () => f.As(f.Admin, new PurgeObject(home)), "contact_merged_in_use");

        // The web saves its document as it is (no Markdown, no linking): the merged contact's id becomes the target's.
        var saved = await f.Pages.SaveAsync(page.Id, new SavePageRequest(page.Title, page.ContentJson, page.Markdown, page.Version), f.Writer, default);
        Assert.Contains(jana.ToString(), saved.ContentJson, StringComparison.Ordinal);
        Assert.DoesNotContain(home.ToString(), saved.ContentJson, StringComparison.Ordinal);
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(saved.Version), f.Writer, default);
        await f.As(f.Admin, new PurgeObject(home));
        Assert.Equal(0L, await ScalarAsync(f, $"""SELECT count(*) FROM "ContentResource" WHERE "Id"='{home}'"""));
    }

    [PostgreSqlFact]
    public async Task An_unresolved_flag_beside_an_object_id_does_not_keep_a_snapshot_from_its_contact()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var jana = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana", $"jana@{Domain()}");
        var forged =JsonNode.Parse(Inline("Ředitel", jana, phone: "+420 111 222 333"))!;
        forged["attrs"]!["data"]!["contacts"]![0]!["unresolved"] = true;

        // A write the server links (a Markdown create over a template's rich content takes this path): the directory's contact holds.
        var page = await f.As<PageDto>(f.Writer, new Atlas.Server.Features.Pages.Commands.CreatePage(
            new CreatePageRequest(f.SpaceId, null, "Lidé", RelationsFixture.Rich(forged.ToJsonString()), "x"), FromMarkdown: true));
        var snapshot = JsonNode.Parse(page.ContentJson)!["document"]!["content"]![0]!["content"]![1]!["attrs"]!["data"]!["contacts"]![0]!.AsObject();
        Assert.Equal(("Jana", jana.ToString()), (snapshot["name"]!.GetValue<string>(), snapshot["objectId"]!.GetValue<string>()));
        Assert.False(snapshot.ContainsKey("phones"));
        Assert.False(snapshot.ContainsKey("unresolved"));
    }

    [PostgreSqlFact]
    public async Task A_merge_locks_the_contacts_merged_into_its_source_before_the_identity_lock_as_a_restore_does()
    {
        // A host of its own: the test asserts the directory-wide advisory lock 'atlas.contact:identity' is free, which every contact
        // write of another test on the shared host takes.
        await using var f = await RelationsFixture.CreateAsync();
        var jana = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana", "jana@example.com");
        var home = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana Domů", "jana@home.example.test");
        var older = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "J. Nováková", "jn@old.example.test");
        await f.As<MergedContacts>(f.Writer, new MergeContacts(older, home));

        // A restore of the older contact holds its row and then wants the identity lock (ObjectTrashHandler).
        await using var restore = new Npgsql.NpgsqlConnection(f.Host.ConnectionString);
        await restore.OpenAsync();
        await using var tx = await restore.BeginTransactionAsync();
        await using (var row = new Npgsql.NpgsqlCommand($"""SELECT 1 FROM "ContentResource" WHERE "Id"='{older}' FOR UPDATE""", restore))
        {
            await row.ExecuteNonQueryAsync();
        }
        var merge = f.As<MergedContacts>(f.Writer, new MergeContacts(home, jana));
        await Task.Delay(500);
        Assert.False(merge.IsCompleted);
        // The merge waits for that row without the identity lock, so the restore gets it: no deadlock.
        await using (var identity = new Npgsql.NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtextextended('atlas.contact:identity', 812))", restore))
        {
            Assert.True((bool)(await identity.ExecuteScalarAsync())!);
        }
        await tx.RollbackAsync();
        Assert.Equal(jana, (await merge).ContactId);
        Assert.Equal(1L, await ScalarAsync(f, $"""SELECT count(*) FROM "ContentResource" WHERE "Id"='{older}' AND "Data"->>'mergedInto'='{jana}'"""));
    }

    [PostgreSqlFact]
    public async Task A_refused_duplicate_names_the_existing_contact_only_to_who_may_read_it()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var mail = $"jana@{Domain()}";
        // The outsider writes in a space of its own, so it writes the wiki directory too, but reads nothing of the fixture's space.
        await f.Host.SeedSpaceAsync("Jiný", (f.Outsider, SpaceRole.Writer));
        var mine = await f.ContactAsync(f.Outsider, ObjectScopes.Wiki, "Jana", mail);
        await f.As(f.Outsider, new TrashObject(mine));
        var hidden = await f.ContactAsync(f.Writer, ObjectScopes.Space, "Jana Tajná", mail);

        var created = await BoardsFixture.Refused(409, () => f.ContactAsync(f.Outsider, ObjectScopes.Wiki, "Jana N.", mail), "duplicate_contact");
        Assert.False(created.Extensions?.ContainsKey("contactId") ?? false);
        var restored = await BoardsFixture.Refused(409, () => f.As(f.Outsider, new RestoreObject(mine)), "contact_restore_duplicate");
        Assert.False(restored.Extensions?.ContainsKey("contactId") ?? false);
        // Who reads the space gets the contact to offer instead.
        Assert.Equal(hidden, (await BoardsFixture.Refused(409, () => f.As(f.Writer, new RestoreObject(mine)), "contact_restore_duplicate")).Extensions!["contactId"]);
    }

    [PostgreSqlFact]
    public async Task An_address_match_is_refused_behind_more_than_fifty_older_contacts_of_the_same_name()
    {
        await using var f = await RelationsFixture.SharedAsync();
        // The test's own name and address: its 51 contacts would crowd every other test's candidates of the name out of the limit.
        var (name, domain) = ($"Jana {Tag()}", Domain());
        Task<ObjectDto> Create(string json) => f.As<ObjectDto>(f.Writer, new CreateObject(ContactObjectType.Id, ObjectScopes.Wiki, null, Snapshot(json)));
        // The same name with the organization is a weak key: older candidates than the limit takes.
        for (var i = 0; i < 51; i++)
        {
            await Create($$"""{"name":"{{name}}","organization":"Modelsoft"}""");
        }
        var jana = (await Create($$"""{"name":"{{name}}","organization":"Modelsoft","emails":["jana@{{domain}}"]}""")).Id;
        var refused = await BoardsFixture.Refused(409, () => Create($$"""{"name":"{{name}}","organization":"Modelsoft","emails":["jana@{{domain}}"]}"""), "duplicate_contact");
        Assert.Equal(jana, refused.Extensions!["contactId"]);
    }

    [PostgreSqlFact]
    public async Task A_match_and_a_link_tell_nothing_of_a_contact_the_account_may_not_read()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var (domain, organization, phone) = (Domain(), "Modelsoft " + Tag(), Phone());
        var hidden = (await f.As<ObjectDto>(f.Writer, new CreateObject(ContactObjectType.Id, ObjectScopes.Space, f.SpaceId,
            Snapshot($$"""{"name":"Jana Tajná","emails":["jana@{{domain}}"],"phones":["{{phone}}"],"organization":"{{organization}}"}""")))).Id;
        var strong = Snapshot($$"""{"name":"Jana","emails":["jana@{{domain}}"],"phones":["602 000 000"]}""");
        var weak = Snapshot($$"""{"name":"Jana Tajná","organization":"{{organization}}"}""");

        // The space's reader gets the person; the outsider only the id the link needs, with its own name and no stored field.
        Assert.Equal("Jana Tajná", (await f.As<ContactMatchResult>(f.Reader, new MatchContact(strong))).Strong!.Name);
        Assert.Equal([hidden], (await f.As<ContactMatchResult>(f.Reader, new MatchContact(weak))).Weak.Select(x => x.ContactId));
        var matched = await f.As<ContactMatchResult>(f.Outsider, new MatchContact(strong));
        Assert.Equal((hidden, "Jana", (JsonObject?)null), (matched.Strong!.ContactId, matched.Strong.Name, matched.Strong.Contact));
        Assert.Empty(matched.Conflicts);
        Assert.Empty((await f.As<ContactMatchResult>(f.Outsider, new MatchContact(weak))).Weak);
        // Of two strong matches the one the account may read comes first, though the other is older.
        var open = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana Veřejná", $"public@{domain}");
        var both = (await f.As<ContactMatchResult>(f.Outsider, new MatchContact(Snapshot($$"""{"name":"Jana","emails":["jana@{{domain}}","public@{{domain}}"]}""")))).Strong!;
        Assert.Equal((open, "Jana Veřejná"), (both.ContactId, both.Name));

        var linked = await f.As<ResolvedContact>(f.Outsider, new ResolveContact(strong));
        Assert.Equal((hidden, false), (linked.ContactId, linked.Created));
        Assert.Equal(("contact_exists", "Jana"), (Assert.Single(linked.Warnings).Type, linked.Warnings[0].Name));
        Assert.DoesNotContain("Tajná", System.Text.Json.JsonSerializer.Serialize(linked), StringComparison.Ordinal);
        Assert.DoesNotContain("777", System.Text.Json.JsonSerializer.Serialize(linked), StringComparison.Ordinal);
    }

    [PostgreSqlFact]
    public async Task The_migration_reports_first_then_links_working_and_published_revisions_in_a_resumable_background_run()
    {
        // A host of its own: the report and the link run read and rewrite every page of the wiki, and the test counts every contact.
        await using var f = await RelationsFixture.CreateAsync();
        await ContactLinkRunTests.GrantEveryoneAsync(f);
        var jana = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana", "jana@example.com");
        await f.As<ObjectChangeResult>(f.Writer, Change(jana, """{"phones":["777 123 456"]}"""));
        // Existing rows as 3.0 left them: snapshots without objectId, a published page and a draft, the same person on both.
        var published = await f.PageAsync("Lidé", inline: [Inline("Petr", null, "petr@x.cz"), Inline("J. N.", null, "JANA@example.com")]);
        var draft = await f.PageAsync("Koncept", publish: false, inline: [Inline("Petr", null, "PETR@x.cz"), Inline("Neznámý", null, null, "+420 777 123 456")]);
        var before = await RevisionsAsync(f);

        await BoardsFixture.Refused(403, () => f.As(f.Writer, new ReportContactLinks()));
        await BoardsFixture.Refused(403, () => f.As(f.Writer, new StartContactLinkRun()));
        var report = await f.As<ContactLinkReport>(f.Admin, new ReportContactLinks());
        Assert.Equal(("report", 2), (report.Mode, report.Pages));
        // The same address is the same person, linked; the name its page wrote otherwise is reported.
        Assert.Equal(["email:jana@example.com"], report.Linked.Select(item => item.IdentityKey));
        Assert.Equal("conflict:name", report.Linked[0].Reason);
        Assert.Equal(jana, report.Linked[0].ContactId);
        Assert.Equal(("email:petr@x.cz", 2, (Guid?)null), (Assert.Single(report.Created).IdentityKey, report.Created[0].Occurrences, report.Created[0].ContactId));
        Assert.Equal(("possible_duplicate", jana), (Assert.Single(report.Unclear).Reason, report.Unclear[0].ContactId!.Value));
        Assert.Equal(before, await RevisionsAsync(f));

        // apply queues the run and answers at once, without reading the pages (its report is empty until its plan is done); a second start
        // answers the queued run. Its first round plans this small wiki whole and creates the contacts.
        var run = await f.As<ContactLinkRunView>(f.Admin, new StartContactLinkRun());
        Assert.Equal(("queued", 0, 0, 0), (run.State, run.Pages, run.PagesDone, run.Report.Created.Count));
        Assert.Equal(run.Id, (await f.As<ContactLinkRunView>(f.Admin, new StartContactLinkRun())).Id);
        Assert.Equal(1, (await f.As<ContactLinkRound>(f.Admin, new ContinueContactLinkRun(run.Id))).Pages);
        var petr = (await f.As<ContactLinkRunView?>(f.Admin, new GetContactLinkRun()))!.Report.Created.Single().ContactId!.Value;
        Assert.Equal(before, await RevisionsAsync(f));
        // A restart after a committed batch continues after its last page: the first page by id is left to it.
        var first = (Guid)(await OutboxTests.ScalarAsync(f.Host.ConnectionString, """SELECT "Id" FROM "Page" ORDER BY "Id" LIMIT 1"""))!;
        await OutboxTests.ExecuteAsync(f.Host.ConnectionString,
            $$"""UPDATE "JobRun" SET "Progress"=jsonb_set(jsonb_set("Progress",'{afterPageId}','"{{first}}"'),'{pagesDone}','1') WHERE "Id"={{run.Id}}""");
        Assert.Equal(1, (await f.As<ContactLinkRound>(f.Admin, new ContinueContactLinkRun(run.Id))).Pages);
        Assert.Equal(before.Where(row => PageOf(f, row.Id) == first), (await RevisionsAsync(f)).Where(row => PageOf(f, row.Id) == first));
        Assert.Equal(0, (await f.As<ContactLinkRound>(f.Admin, new ContinueContactLinkRun(run.Id))).Pages);
        // The job takes the first run from its checkpoint to the end; a second run links what the first was not given and creates no second Petr.
        // The job runs as the admin who started each run.
        f.Host.Actor.Current = null;
        var runner = f.Host.Services.GetRequiredService<IJobRunner>();
        await runner.RunToEndAsync(ContactLinkJob.JobId, "Test");
        var done = (await f.As<ContactLinkRunView?>(f.Admin, new GetContactLinkRun()))!;
        Assert.Equal((run.Id, "succeeded", 2), (done.Id, done.State, done.PagesDone));
        Assert.NotNull(done.FinishedAt);
        await f.As<ContactLinkRunView>(f.Admin, new StartContactLinkRun());
        f.Host.Actor.Current = null;
        await runner.RunToEndAsync(ContactLinkJob.JobId, "Test");
        Assert.Equal("succeeded", (await f.As<ContactLinkRunView?>(f.Admin, new GetContactLinkRun()))!.State);

        var after = await RevisionsAsync(f);
        // Every revision holding a person to link, the published and the working ones, each with its objectIds.
        Assert.All(after.Where(row => row.Json.Contains("Petr")), row => Assert.Contains($"\"objectId\":\"{petr}\"", row.Json));
        Assert.Contains(after, row => row.Json.Contains($"\"objectId\":\"{jana}\""));
        // The unclear one stays as it was; nothing else in the text changed.
        Assert.Contains(after, row => row.Json.Contains("Neznámý") && !row.Json.Contains("\"name\":\"Neznámý\",\"phones\":[\"+420 777 123 456\"],\"objectId\""));
        Assert.All(after.Zip(before), pair => Assert.True(JsonNode.DeepEquals(Text(pair.Second.Json), Text(pair.First.Json))));
        // Jana and the new Petr; the unclear one created nothing.
        Assert.Equal(2L, await ScalarAsync(f, """SELECT count(*) FROM "ContentResource" WHERE "TypeId"='atlas.contact' AND "TrashedAt" IS NULL"""));
        // The revisions rewritten in place are projected again by the run's own batches (LinkBatchAsync refreshes each changed page): Petr
        // is mentioned by the published page and by the draft's working revision, so his usage sees both (drafts counted).
        Assert.Equal(2L, await ScalarAsync(f, $"""SELECT count(DISTINCT "SourcePageId") FROM "PageObjectMention" WHERE "ObjectId"='{petr}'"""));
        Assert.Equal(1L, await ScalarAsync(f, $"""
            SELECT count(*) FROM "PageObjectMention" m JOIN "PageRevision" r ON r."Id"=m."SourceRevisionId"
            WHERE m."ObjectId"='{petr}' AND m."SourcePageId"='{draft.Id}' AND r."ActiveTo" IS NULL
            """));
        Assert.Equal(2, (await f.As<UsageCount>(f.Writer, new CountObjectUsage(petr))).Total);
        // The list with its reasons reads the never-published draft too (no publication to compare with).
        var petrUses = await f.As<ObjectUsageList>(f.Writer, new GetObjectUsage(petr));
        Assert.Equal([(published.Id, false), (draft.Id, true)], petrUses.Items.Select(row => (row.PlaceId, row.Draft)).OrderBy(row => row.Draft));
        // Run again, there is nothing left to link but the unclear one.
        var again = await f.As<ContactLinkReport>(f.Admin, new ReportContactLinks());
        Assert.Equal((0, 0, 1), (again.Linked.Count, again.Created.Count, again.Unclear.Count));
        Assert.NotNull(published);
    }

    [PostgreSqlFact]
    public async Task After_migration_2026103100_an_existing_phone_only_contact_is_keyed_by_its_phone()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_contacts_identity_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026103100);
        const string Owner = "00000000-0000-0000-0000-000000000001";
        const string Page = "00000000-0000-0000-0000-0000000000a1";
        var content = """{"format":"atlas-rich","version":1,"plugins":[{"id":"contacts","version":2}],"document":{"type":"doc","content":[{"type":"contactCard","attrs":{"data":{"version":1,"contacts":[{"name":"Petr","phones":["777 000 111"]}]}}}]}}""";
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Page}','00000000-0000-0000-0000-00000000000a','{Owner}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              VALUES(gen_random_uuid(),'{Page}',1,'Lidé','{content}','x','{Owner}',now(),'web');
            INSERT INTO "EntityOccurrence"("ResourceId","RevisionKind","Kind","IdentityKey","Attributes") VALUES('{Page}','working','contact','name:petr|','{"{"}"name":"Petr"{"}"}');
            DELETE FROM "OutboxMessage";
            """);
        // Wolverine's storage (2026110401) before the durable host starts.
        await AtlasMigrations.MigrateAsync(schema.ConnectionString);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled, outbox: true);
        await OutboxTests.DeliverUpgradedAsync(host);
        Assert.Equal("working:phone:+420777000111", await OutboxTests.ScalarAsync(host.ConnectionString,
            """SELECT string_agg("RevisionKind" || ':' || "IdentityKey", ',') FROM "EntityOccurrence" """));
    }

    /// <summary>Values of one test: on the shared host the directory is every test's, refuses a second person of an address (K1) and
    /// matches weakly by a phone or by a name with its organization.</summary>
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static string Domain() => Tag() + ".example.com";

    /// <summary>A phone of one test (its keys compare the last nine digits).</summary>
    private static string Phone() => $"777 {Random.Shared.Next(1_000_000):000 000}";

    private static JsonObject Snapshot(string json) => JsonNode.Parse(json)!.AsObject();

    private static ApplyObjectChange Change(Guid id, string fields) => new(id, Guid.NewGuid(), null, new JsonObject { ["fields"] = JsonNode.Parse(fields) });

    private static string Inline(string name, Guid? objectId, string? email = null, string? phone = null)
    {
        var contact = new JsonObject { ["name"] = name };
        if (email is not null) contact["emails"] = new JsonArray(email);
        if (phone is not null) contact["phones"] = new JsonArray(phone);
        if (objectId is not null) contact["objectId"] = objectId.ToString();
        return new JsonObject { ["type"] = "contactInline", ["attrs"] = new JsonObject { ["data"] = new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(contact) } } }
            .ToJsonString();
    }

    /// <summary>The text of a stored document without its references: what the migration must never change.</summary>
    private static JsonNode Text(string json)
    {
        var root = JsonNode.Parse(json)!;
        Strip(root);
        return root;

        static void Strip(JsonNode? node)
        {
            if (node is JsonObject element)
            {
                element.Remove("objectId");
                foreach (var (_, child) in element) Strip(child);
            }
            else if (node is JsonArray items)
            {
                foreach (var child in items) Strip(child);
            }
        }
    }

    private static async Task<IReadOnlyList<(Guid Id, string Json)>> RevisionsAsync(RelationsFixture f)
    {
        await using var connection = new Npgsql.NpgsqlConnection(f.Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("""SELECT "Id","ContentJson" FROM "PageRevision" WHERE "ContentJson" LIKE '%contactInline%' ORDER BY "Id" """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(Guid, string)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1)));
        }
        return rows;
    }

    private static Guid PageOf(RelationsFixture f, Guid revision) =>
        (Guid)OutboxTests.ScalarAsync(f.Host.ConnectionString, $"""SELECT "PageId" FROM "PageRevision" WHERE "Id"='{revision}'""").GetAwaiter().GetResult()!;

    private static async Task<long> ScalarAsync(RelationsFixture f, string sql) => (long)(await OutboxTests.ScalarAsync(f.Host.ConnectionString, sql))!;

    private static Task DrainAsync(RelationsFixture f) => OutboxTests.DrainAsync(f.Host);
}
