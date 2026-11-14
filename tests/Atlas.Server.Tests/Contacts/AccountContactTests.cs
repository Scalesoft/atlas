using System.Text.Json.Nodes;
using Atlas.Server.Features.Auth.OpenIdConnect.Commands;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Contacts.Commands;
using Atlas.Server.Features.Contacts.Contracts;
using Atlas.Server.Features.Contacts.LinkRun;
using Atlas.Server.Features.Contacts.LinkRun.Commands;
using Atlas.Server.Features.Contacts.Queries;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Queries;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.Relations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Contacts;

/// <summary>Every account has one directory contact (owner decision R3, 2026-10-31): matched in the background by address, created when
/// there is no candidate, the rest computed as pairs for a wiki admin when read (nothing stored beside the contacts); the admin links a pair,
/// or dismisses it by giving the account its own contact.</summary>
// Hosts of their own (RelationsFixture.CreateAsync, not SharedAsync): every test sweeps every account of the installation (SweepAccountContacts,
// the job) and counts all contacts, which on the shared host would link and create contacts for other tests' accounts.
public sealed class AccountContactTests
{
    [PostgreSqlFact]
    public async Task Every_account_is_linked_by_address_suggested_by_name_or_given_a_contact_and_a_second_sweep_changes_nothing()
    {
        await using var f = await RelationsFixture.CreateAsync();
        var jana = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana Nováková", "jana@example.com");
        var petr = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Petr Svoboda");
        var eva = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Eva Malá", "eva@old.example.test");
        var eva2 = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "eva  mala");
        // A contact of another account is that person's, never a candidate; a private one is nobody's directory contact.
        var taken = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Ota Jiný");
        await SqlAsync(f, $"""UPDATE "ContentResource" SET "Data" = jsonb_set("Data", '{"{"}directoryId{"}"}', '"{f.Reader.Id}"') WHERE "Id" = '{taken}'""");
        await f.ContactAsync(f.Writer, ObjectScopes.Private, "Nikdo Nový", "nikdo@example.com");

        var a = await AccountAsync(f, "Jana Nováková", " JANA@example.com ");
        var b = await AccountAsync(f, "Petr Svoboda", "petr@example.test");
        var c = await AccountAsync(f, "Eva Malá", null);
        var d = await AccountAsync(f, "Nikdo Nový", "nikdo@example.com");
        var e = await AccountAsync(f, "Ota Jiný", null);
        var inactive = await AccountAsync(f, "Bývalý Kolega", null, active: false);

        await f.Host.InvokeAsync<int>(new SweepAccountContacts());

        // (a) exactly one contact of the address: linked, the account in its directoryId.
        Assert.Equal(a.ToString(), await DirectoryIdAsync(f, jana));
        // (b) the same name only and (c) several: offered as pairs when read, not linked.
        Assert.Null(await DirectoryIdAsync(f, petr));
        Assert.Equal($"{b}:{petr}:name", await PairsAsync(f, b));
        Assert.Equal(string.Join(",", new[] { eva, eva2 }.Order().Select(id => $"{c}:{id}:name")), await PairsAsync(f, c));
        // (d) none: a wiki contact of the account with its name and address; the taken contact stays the other account's.
        Assert.Equal("Nikdo Nový|nikdo@example.com|wiki", await ScalarAsync(f,
            $"""SELECT "Title" || '|' || ("Data"->'emails'->>0) || '|' || "Scope" FROM "ContentResource" WHERE "Data"->>'directoryId' = '{d}'"""));
        Assert.Equal(1L, await ScalarAsync(f, $"""SELECT count(*) FROM "ContentResource" WHERE "Data"->>'directoryId' = '{e}'"""));
        Assert.Equal(f.Reader.Id.ToString(), await DirectoryIdAsync(f, taken));
        // The fixture's accounts got theirs too; a deactivated account is not matched anew.
        Assert.Equal(1L, await ScalarAsync(f, $"""SELECT count(*) FROM "ContentResource" WHERE "Data"->>'directoryId' = '{f.Admin.Id}'"""));
        Assert.Equal(0L, await ScalarAsync(f, $"""SELECT count(*) FROM "ContentResource" WHERE "Data"->>'directoryId' = '{inactive}'"""));

        var contacts = await ScalarAsync(f, """SELECT count(*) FROM "ContentResource" WHERE "TypeId" = 'atlas.contact'""");
        Assert.Equal(0, await f.Host.InvokeAsync<int>(new SweepAccountContacts()));
        Assert.Equal(contacts, await ScalarAsync(f, """SELECT count(*) FROM "ContentResource" WHERE "TypeId" = 'atlas.contact'"""));
        // A deactivated account keeps its contact.
        await SqlAsync(f, $"""UPDATE "UserAccount" SET "IsActive" = false WHERE "Id" = '{a}'""");
        Assert.Equal(0, await f.Host.InvokeAsync<int>(new SweepAccountContacts()));
        Assert.Equal(a.ToString(), await DirectoryIdAsync(f, jana));
    }

    [PostgreSqlFact]
    public async Task The_job_sweeps_in_batches_until_every_account_has_its_contact_and_a_second_run_changes_nothing()
    {
        await using var f = await RelationsFixture.CreateAsync();
        var accounts = new List<Guid>();
        for (var i = 0; i < AccountContacts.SweepBatch + 5; i++)
        {
            accounts.Add(await AccountAsync(f, $"Osoba {i}", $"osoba{i}@example.com"));
        }

        var runner = f.Host.Services.GetRequiredService<IJobRunner>();
        await runner.RunToEndAsync(AccountContactsJob.JobId, "Test");

        // More accounts than one batch: all of them (and the fixture's) have their contact, in one run.
        Assert.Equal(0L, await ScalarAsync(f, """
            SELECT count(*) FROM "UserAccount" a WHERE a."IsActive" AND NOT EXISTS (SELECT 1 FROM "ContentResource" c WHERE c."TypeId"='atlas.contact' AND c."Data"->>'directoryId' = CAST(a."Id" AS text))
            """));
        Assert.Equal("succeeded", await ScalarAsync(f, """SELECT "State" FROM "JobRun" WHERE "JobId"='contacts.accounts' ORDER BY "Id" DESC LIMIT 1"""));
        var contacts = await ScalarAsync(f, """SELECT count(*) FROM "ContentResource" WHERE "TypeId" = 'atlas.contact'""");
        await runner.RunToEndAsync(AccountContactsJob.JobId, "Test");
        Assert.Equal(contacts, await ScalarAsync(f, """SELECT count(*) FROM "ContentResource" WHERE "TypeId" = 'atlas.contact'"""));
    }

    [PostgreSqlFact]
    public async Task A_sweep_locks_its_contacts_before_the_identity_lock_so_an_edit_holding_a_later_one_never_deadlocks_with_it()
    {
        await using var f = await RelationsFixture.CreateAsync();
        var anna = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Anna Malá", "anna@example.com");
        var bara = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Bára Velká", "bara@example.com");
        await AccountAsync(f, "Anna Malá", "anna@example.com");
        // Bára's account last in the batch (by id): before the fix every other account's link or contact took the identity lock first.
        var last = await AccountAsync(f, "Bára Velká", "bara@example.com", id: Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));

        // An edit of Bára's contact holds its row and then wants the identity lock (ApplyObjectChange, ContactObjectType).
        await using var edit = new Npgsql.NpgsqlConnection(f.Host.ConnectionString);
        await edit.OpenAsync();
        await using var tx = await edit.BeginTransactionAsync();
        await using (var row = new Npgsql.NpgsqlCommand($"""SELECT 1 FROM "ContentResource" WHERE "Id"='{bara}' FOR UPDATE""", edit))
        {
            await row.ExecuteNonQueryAsync();
        }
        var sweep = f.Host.InvokeAsync<int>(new SweepAccountContacts());
        await Task.Delay(500);
        Assert.False(sweep.IsCompleted);
        // The sweep waits for that row without the identity lock, so the edit gets it and ends: no deadlock, both finish.
        await using (var identity = new Npgsql.NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtextextended('atlas.contact:identity', 812))", edit))
        {
            Assert.True((bool)(await identity.ExecuteScalarAsync())!);
        }
        await tx.CommitAsync();
        Assert.True(await sweep > 0);
        Assert.Equal(last.ToString(), await DirectoryIdAsync(f, bara));
        Assert.NotNull(await DirectoryIdAsync(f, anna));
    }

    [PostgreSqlFact]
    public async Task Several_contacts_of_the_address_are_suggested_and_a_first_oidc_sign_in_with_a_verified_address_is_linked()
    {
        await using var f = await RelationsFixture.CreateAsync();
        var jana = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana Nováková", "jana@example.com");
        var user = await f.Host.InvokeAsync<UserAccount>(new ResolveExternalIdentity("https://hub.example.test/hub", true, "hub-jana", "Jana N.", null, null, false, "jana@example.com"));
        Assert.Equal("jana@example.com", user.Email);
        await f.Host.InvokeAsync<int>(new SweepAccountContacts());
        Assert.Equal(user.Id.ToString(), await DirectoryIdAsync(f, jana));

        // An address that is no address stays off the account.
        var odd = await f.Host.InvokeAsync<UserAccount>(new ResolveExternalIdentity("https://hub.example.test/hub", true, "hub-odd", "Odd", null, null, false, "not an address"));
        Assert.Null(odd.Email);
    }

    [PostgreSqlFact]
    public async Task Only_a_wiki_admin_sees_and_decides_pairs_a_dismissal_gives_the_account_its_contact_and_is_undone_and_a_link_merges_it()
    {
        await using var f = await RelationsFixture.CreateAsync();
        var petr = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Petr Svoboda");
        var eva = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Eva Malá", "eva@old.example.test");
        var eva2 = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Eva Malá");
        var b = await AccountAsync(f, "Petr Svoboda", "petr@example.test");
        var c = await AccountAsync(f, "Eva Malá", null);
        await f.Host.InvokeAsync<int>(new SweepAccountContacts());

        Assert.Empty((await f.As<AccountContactSuggestionList>(f.Writer, new ListAccountContactSuggestions())).Items);
        await BoardsFixture.Refused(403, () => f.As(f.Writer, new ResolveAccountContact(b, petr, "link")), "forbidden");
        var items = (await f.As<AccountContactSuggestionList>(f.Admin, new ListAccountContactSuggestions())).Items;
        Assert.Equal(3, items.Count);
        var suggestion = Assert.Single(items, item => item.AccountId == b);
        Assert.Equal(("Petr Svoboda", "petr@example.test", petr, "name", (Guid?)null), (suggestion.AccountName, suggestion.AccountEmail, suggestion.Contact.ContactId, suggestion.Reason, suggestion.OwnContactId));
        // Two accounts of one name differ by their username (P8).
        Assert.StartsWith("u", suggestion.AccountUsername);

        // „Propojit“ without a contact of the account: the chosen one becomes it; the sweep leaves the account alone then.
        await f.As<ResolvedAccountContact>(f.Admin, new ResolveAccountContact(b, petr, "link"));
        Assert.Equal(b.ToString(), await DirectoryIdAsync(f, petr));
        await BoardsFixture.Refused(404, () => f.As(f.Admin, new ResolveAccountContact(b, petr, "dismiss")), "not_found");

        // „Není to on“ gives the account its own contact, so the decision is in the data: no pair of the account is offered and the sweep
        // leaves it; „Zpět“ (restore with that contact) trashes it and the pairs are back.
        var own = (await f.As<ResolvedAccountContact>(f.Admin, new ResolveAccountContact(c, eva, "dismiss"))).CreatedContactId!.Value;
        Assert.Equal(c.ToString(), await DirectoryIdAsync(f, own));
        Assert.Empty((await f.As<AccountContactSuggestionList>(f.Admin, new ListAccountContactSuggestions())).Items);
        Assert.Equal(0, await f.Host.InvokeAsync<int>(new SweepAccountContacts()));
        await BoardsFixture.Refused(404, () => f.As(f.Admin, new ResolveAccountContact(c, eva2, "dismiss")), "not_found");
        await f.As<ResolvedAccountContact>(f.Admin, new ResolveAccountContact(c, own, "restore"));
        Assert.Equal(1L, await ScalarAsync(f, $"""SELECT count(*) FROM "ContentResource" WHERE "Id" = '{own}' AND "TrashedAt" IS NOT NULL"""));
        Assert.Equal(2, (await f.As<AccountContactSuggestionList>(f.Admin, new ListAccountContactSuggestions())).Items.Count);
        await BoardsFixture.Refused(404, () => f.As(f.Admin, new ResolveAccountContact(c, eva, "restore")), "not_found");

        // Dismissed again, the admin changes their mind: „Propojit“ merges the account's own contact into the chosen one, the mentions of it
        // following.
        own = (await f.As<ResolvedAccountContact>(f.Admin, new ResolveAccountContact(c, eva2, "dismiss"))).CreatedContactId!.Value;
        var page = await f.PageAsync("Lidé", inline: [Inline("Eva Malá", own)]);
        var linked = await f.As<ResolvedAccountContact>(f.Admin, new ResolveAccountContact(c, eva, "link"));
        Assert.Equal(own, linked.MergedContactId);
        Assert.Equal(c.ToString(), await DirectoryIdAsync(f, eva));
        Assert.Equal(1L, await ScalarAsync(f, $"""SELECT count(*) FROM "ContentResource" WHERE "Id" = '{own}' AND "TrashedAt" IS NOT NULL AND "Data"->>'mergedInto' = '{eva}'"""));
        Assert.Contains(page.Id, (await f.As<ObjectUsageList>(f.Admin, new GetObjectUsage(eva))).Items.Select(row => row.PlaceId));
        Assert.Empty((await f.As<AccountContactSuggestionList>(f.Admin, new ListAccountContactSuggestions())).Items);
        Assert.Equal(0, await f.Host.InvokeAsync<int>(new SweepAccountContacts()));
        await BoardsFixture.Refused(409, () => f.As(f.Admin, new ResolveAccountContact(b, eva, "link")), "contact_linked");
    }

    [PostgreSqlFact]
    public async Task After_migration_2026110190_the_existing_accounts_get_their_contacts_and_the_rest_is_offered()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_account_contacts_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110190);
        const string Jana = "00000000-0000-0000-0000-000000000001", Petr = "00000000-0000-0000-0000-000000000002", Nikdo = "00000000-0000-0000-0000-000000000003";
        const string JanaContact = "00000000-0000-0000-0000-0000000000c1", PetrContact = "00000000-0000-0000-0000-0000000000c2";
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","Email","IsWikiAdmin","IsActive","SecurityStamp") VALUES
              ('{Jana}','jana','Jana Nováková','jana@example.com',true,true,'s1'),
              ('{Petr}','petr','Petr Svoboda',NULL,false,true,'s2'),
              ('{Nikdo}','nikdo','Nikdo Nový',NULL,false,true,'s3');
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","Title","Data") VALUES
              ('{JanaContact}','atlas.contact','wiki',NULL,NULL,1,now(),'{Jana}',now(),'Jana Nováková','{"{"}"name":"Jana Nováková","emails":["Jana@Example.com"]{"}"}'),
              ('{PetrContact}','atlas.contact','wiki',NULL,NULL,1,now(),'{Jana}',now(),'Petr Svoboda','{"{"}"name":"Petr Svoboda"{"}"}');
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        Assert.Equal(2, await host.InvokeAsync<int>(new SweepAccountContacts()));
        Assert.Equal(Jana, await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "Data"->>'directoryId' FROM "ContentResource" WHERE "Id" = '{JanaContact}'"""));
        using (var session = host.OpenSession())
        {
            host.Actor.Current = await session.GetAsync<UserAccount>(Guid.Parse(Jana));
        }
        var pair = Assert.Single((await host.InvokeAsync<AccountContactSuggestionList>(new ListAccountContactSuggestions())).Items);
        Assert.Equal((Guid.Parse(Petr), Guid.Parse(PetrContact), "name"), (pair.AccountId, pair.Contact.ContactId, pair.Reason));
        Assert.Equal("Nikdo Nový", await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "Title" FROM "ContentResource" WHERE "Data"->>'directoryId' = '{Nikdo}'"""));
        Assert.Equal(0, await host.InvokeAsync<int>(new SweepAccountContacts()));
    }

    [PostgreSqlFact]
    public async Task Migration_2026110330_drops_the_stored_suggestions_and_the_pairs_are_computed_again()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_account_contacts_drop_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110330);
        const string Admin = "00000000-0000-0000-0000-000000000001", Petr = "00000000-0000-0000-0000-000000000002";
        const string AdminContact = "00000000-0000-0000-0000-0000000000c1", PetrContact = "00000000-0000-0000-0000-0000000000c2";
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","Email","IsWikiAdmin","IsActive","SecurityStamp") VALUES
              ('{Admin}','admin','Správce',NULL,true,true,'s1'),
              ('{Petr}','petr','Petr Svoboda',NULL,false,true,'s2');
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","Title","Data") VALUES
              ('{AdminContact}','atlas.contact','wiki',NULL,NULL,1,now(),'{Admin}',now(),'Správce','{"{"}"name":"Správce","directoryId":"{Admin}"{"}"}'),
              ('{PetrContact}','atlas.contact','wiki',NULL,NULL,1,now(),'{Admin}',now(),'Petr Svoboda','{"{"}"name":"Petr Svoboda"{"}"}');
            -- A dismissed pair of the old store: dropped, so the pair is offered again (the admin dismisses it by giving Petr his contact).
            INSERT INTO "AccountContactSuggestion"("AccountId","ContactId","Reason","DismissedAt","DismissedById") VALUES ('{Petr}','{PetrContact}','name',now(),'{Admin}');
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        Assert.Equal(0L, await OutboxTests.ScalarAsync(host.ConnectionString, """SELECT count(*) FROM information_schema.tables WHERE table_name = 'AccountContactSuggestion'"""));
        Assert.Equal(2L, await OutboxTests.ScalarAsync(host.ConnectionString, """SELECT count(*) FROM "ContentResource" WHERE "TypeId" = 'atlas.contact'"""));
        Assert.Equal(0, await host.InvokeAsync<int>(new SweepAccountContacts()));
        using (var session = host.OpenSession())
        {
            host.Actor.Current = await session.GetAsync<UserAccount>(Guid.Parse(Admin));
        }
        Assert.Equal([Guid.Parse(PetrContact)], (await host.InvokeAsync<AccountContactSuggestionList>(new ListAccountContactSuggestions())).Items.Select(x => x.Contact.ContactId));
    }

    private static async Task<Guid> AccountAsync(RelationsFixture f, string name, string? email, bool active = true, Guid? id = null)
    {
        var account = new UserAccount { Id = id ?? Guid.NewGuid(), Username = "u" + Guid.NewGuid().ToString("N")[..8], DisplayName = name, Email = email, IsActive = active };
        await f.Host.SeedAsync(session => session.SaveAsync(account));
        return account.Id;
    }

    private static async Task<string?> DirectoryIdAsync(RelationsFixture f, Guid contact)
        => await OutboxTests.ScalarAsync(f.Host.ConnectionString, $"""SELECT "Data"->>'directoryId' FROM "ContentResource" WHERE "Id" = '{contact}'""") as string;

    private static async Task<string> PairsAsync(RelationsFixture f, Guid account)
        => string.Join(",", (await f.As<AccountContactSuggestionList>(f.Admin, new ListAccountContactSuggestions())).Items.Where(x => x.AccountId == account)
            .OrderBy(x => x.Contact.ContactId).Select(x => $"{x.AccountId}:{x.Contact.ContactId}:{x.Reason}"));

    private static Task<object?> ScalarAsync(RelationsFixture f, string sql) => OutboxTests.ScalarAsync(f.Host.ConnectionString, sql);

    private static Task SqlAsync(RelationsFixture f, string sql) => OutboxTests.ExecuteAsync(f.Host.ConnectionString, sql);

    private static string Inline(string name, Guid objectId) =>
        new JsonObject { ["type"] = "contactInline", ["attrs"] = new JsonObject { ["data"] = new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(
            new JsonObject { ["name"] = name, ["objectId"] = objectId.ToString() }) } } }.ToJsonString();
}
