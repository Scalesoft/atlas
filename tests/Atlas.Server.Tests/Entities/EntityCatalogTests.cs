using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Entities;
using Atlas.Server.Features.Entities.Commands;
using Atlas.Server.Features.Entities.Queries;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.Relations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Entities;

/// <summary>The entity catalog of contacts (plan §14): one extractor, filled through the outbox, read under the actor's rights.</summary>
public sealed class EntityCatalogTests
{
    private static string Card(string name, string? email = null, string? organization = null, bool extra = false) =>
        Card(Contact(name, email, organization, extra));

    private static string Card(JsonObject contact) =>
        new JsonObject
        {
            ["type"] = "contactCard",
            ["attrs"] = new JsonObject { ["data"] = new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(contact) } },
        }.ToJsonString();

    private static JsonObject Contact(string name, string? email, string? organization, bool extra)
    {
        var contact = new JsonObject { ["name"] = name };
        if (extra) contact["secret"] = "dropped";
        if (email is not null) contact["emails"] = new JsonArray(email);
        if (organization is not null) contact["organization"] = organization;
        return contact;
    }

    private static string Rich(params string[] blocks) =>
        """{"format":"atlas-rich","version":1,"plugins":[{"id":"contacts","version":2},{"id":"heading-anchors","version":1}],"document":{"type":"doc","content":["""
        + """{"type":"heading","attrs":{"level":2,"headingId":"tym"},"content":[{"type":"text","text":"Tým"}]}"""
        + string.Concat(blocks.Select(block => "," + block)) + "]}}";

    [Fact]
    public void The_one_extractor_reads_cards_and_nested_mentions_under_their_heading_and_keys_identities()
    {
        var json = Rich(Card("Jana", "Jana@Example.com", extra: true),
            """{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[""" +
            RelationsFixture.ContactInline("petr@example.com") + "]}]}]}");
        var found = ContactSnapshots.Read(json);

        Assert.Equal(new[] { ("Jana", "tym"), ("Někdo", "tym") }, found.Select(x => (x.Contact["name"]!.GetValue<string>(), x.HeadingId!)));
        Assert.False(found[0].Contact.ContainsKey("secret"));
        Assert.Equal("email:jana@example.com", EntityIdentity.Of(found[0].Contact));
        Assert.Equal("name:jan kovarik|ab klinika", EntityIdentity.Of(new JsonObject { ["name"] = "Jan  Kovařík", ["organization"] = "AB Klinika" }));
        Assert.Equal("user:u1", EntityIdentity.Of(new JsonObject { ["name"] = "X", ["internalUser"] = new JsonObject { ["id"] = "U1" } }));
    }

    [Fact]
    public void An_unresolved_name_is_no_person_but_a_snapshot_with_an_object_id_is_its_contact()
    {
        const string Id = "0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b";
        var found = ContactSnapshots.Read(Rich(Card(new JsonObject { ["name"] = "Jan Novák", ["unresolved"] = true }),
            Card(new JsonObject { ["name"] = "Jana", ["objectId"] = Id, ["unresolved"] = true })));

        Assert.Equal($"object:{Id}", EntityIdentity.Of(Assert.Single(found).Contact));
    }

    [PostgreSqlFact]
    public async Task After_migration_2026110101_an_unresolved_name_leaves_the_catalog()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_entities_unresolved_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110101);
        const string Owner = "00000000-0000-0000-0000-000000000001";
        const string Page = "00000000-0000-0000-0000-0000000000a1";
        var content = Rich(Card(new JsonObject { ["name"] = "Jan Novák", ["unresolved"] = true }), Card("Jana", "jana@example.com")).Replace("'", "''");
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Page}','00000000-0000-0000-0000-00000000000a','{Owner}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              VALUES(gen_random_uuid(),'{Page}',1,'Lidé','{content}','x','{Owner}',now(),'web');
            INSERT INTO "EntityOccurrence"("ResourceId","RevisionKind","Kind","IdentityKey","Attributes")
              VALUES('{Page}','working','contact','name:jan novak|','{"{"}"name":"Jan Novák"{"}"}'),('{Page}','working','contact','email:jana@example.com','{"{"}"name":"Jana"{"}"}');
            DELETE FROM "OutboxMessage";
            """);
        // Wolverine's storage (2026110401) before the durable host starts.
        await AtlasMigrations.MigrateAsync(schema.ConnectionString);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled, outbox: true);
        await OutboxTests.DeliverUpgradedAsync(host);
        Assert.Equal("working:email:jana@example.com", await OutboxTests.ScalarAsync(host.ConnectionString,
            """SELECT string_agg("RevisionKind" || ':' || "IdentityKey", ',') FROM "EntityOccurrence" """));
    }

    [PostgreSqlFact]
    [Trait("Category", "Smoke")]
    public async Task Readers_see_the_published_occurrences_writers_the_working_ones_merged_with_the_directory_and_nothing_beyond_their_rights()
    {
        await using var f = await RelationsFixture.CreateAsync(outbox: true);
        var page = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Dodavatelé", Rich(Card("Jana Nováková", "jana@example.com", "Modelsoft")), "x"), f.Writer, default);
        page = await f.Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        // A working change nobody has published: only those who edit the page see it.
        await f.Pages.SaveAsync(page.Id, new SavePageRequest("Dodavatelé", Rich(Card("Jana Nováková", "jana@example.com", "Modelsoft"), Card("Petr Draft", "petr@example.com")), null,
            page.Version), f.Writer, default);
        var directory = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana N.", "JANA@example.com");
        await DrainAsync(f);

        var reader = await ListAsync(f, f.Reader);
        var jana = Assert.Single(reader.Items);
        Assert.Equal(1, reader.Total);
        Assert.Equal("email:jana@example.com", jana.IdentityKey);
        // The directory holds the identity, so its card is the entity's (SEC1-3): the page's organization stays the page's; both sources stay.
        Assert.Equal("Jana N.", jana.Attributes["name"]!.GetValue<string>());
        Assert.False(jana.Attributes.ContainsKey("organization"));
        Assert.Equal(new[] { (directory, "atlas.contact", (string?)null, "published"), (page.Id, "atlas.page", "tym", "published") },
            jana.Sources.Select(s => (s.ResourceId, s.ResourceType, s.Anchor, s.RevisionKind)));

        var writer = await ListAsync(f, f.Writer);
        Assert.Equal(new[] { "Jana N.", "Petr Draft" }, writer.Items.Select(i => i.Attributes["name"]!.GetValue<string>()));
        // „rozpracováno“ only where the published revision lacks the person (UX3-3): Jana is published, Petr only in the draft.
        Assert.Equal(new[] { "published", "working" }, writer.Items.Select(i => i.Sources.Single(s => s.ResourceType == "atlas.page").RevisionKind));
        Assert.Equal(new[] { "Petr Draft" }, (await ListAsync(f, f.Writer, q: "PETR@")).Items.Select(i => i.Attributes["name"]!.GetValue<string>()));
        Assert.Equal(2, (await ListAsync(f, f.Writer, "resource", page.Id)).Total);
        Assert.Equal(2, (await ListAsync(f, f.Writer, "subtree", page.Id)).Total);
        Assert.Equal(1, (await ListAsync(f, f.Reader, "space", f.SpaceId)).Total);

        // No role in the space: only the wiki directory is readable, and the count tells nothing more.
        var outsider = await ListAsync(f, f.Outsider);
        Assert.Equal((1, "atlas.contact"), (outsider.Total, Assert.Single(Assert.Single(outsider.Items).Sources).ResourceType));
        Assert.Equal(0, (await ListAsync(f, f.Outsider, "space", f.SpaceId)).Total);

        // MCP: a space switched out of AI leaves its pages out; the wiki directory stays.
        await f.Host.ExecuteAsync("""INSERT INTO "SpaceAiSetting"("Id","SpaceId","Included","ChangedById","ActiveFrom") SELECT gen_random_uuid(),:id,false,"Id",now() FROM "UserAccount" WHERE "IsWikiAdmin" LIMIT 1""", f.SpaceId);
        f.Host.Channel.Current = new RequestChannel(ContentChannel.Mcp, Guid.NewGuid());
        var mcp = await ListAsync(f, f.Writer);
        Assert.Equal(new[] { "atlas.contact" }, mcp.Items.SelectMany(i => i.Sources).Select(s => s.ResourceType));
        f.Host.Channel.Current = RequestChannel.Web;

        // The page in the trash takes its occurrences out of every list.
        await f.TrashPageAsync(await f.Pages.GetAsync(page.Id, f.Writer, default), f.Writer);
        Assert.Equal(new[] { "atlas.contact" }, (await ListAsync(f, f.Writer)).Items.SelectMany(i => i.Sources).Select(s => s.ResourceType));
    }

    [PostgreSqlFact]
    public async Task A_page_naming_a_directory_person_adds_a_source_and_never_a_phone_or_field_of_its_own()
    {
        await using var f = await RelationsFixture.CreateAsync(outbox: true);
        var directory = await f.As<ObjectDto>(f.Writer, new CreateObject(ContactObjectType.Id, ObjectScopes.Wiki, null, new JsonObject
        {
            ["name"] = "Jana Nováková", ["emails"] = new JsonArray("jana@example.com"), ["phones"] = new JsonArray("+420 111 222 333"),
        }));
        // Any page writer can name the person's e-mail on a card of their own, with a phone and an address of their choosing.
        var page = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Podvrh", Rich(Card(new JsonObject
        {
            ["name"] = "Jana", ["title"] = "Účetní", ["emails"] = new JsonArray("JANA@example.com", "jinam@example.com"),
            ["phones"] = new JsonArray("+420 999 888 777"), ["address"] = "Jinde 1",
        })), "x"), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        // A person only pages name still merges their cards: the phones of both.
        var other = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Tým", Rich(
            Card(new JsonObject { ["name"] = "Petr", ["emails"] = new JsonArray("petr@example.com"), ["phones"] = new JsonArray("+420 111 000 001") }),
            Card(new JsonObject { ["name"] = "Petr B.", ["emails"] = new JsonArray("petr@example.com"), ["phones"] = new JsonArray("+420 111 000 002") })), "x"), f.Writer, default);
        await f.Pages.PublishAsync(other.Id, new PublishPageRequest(other.Version), f.Writer, default);
        await DrainAsync(f);

        var list = await ListAsync(f, f.Reader);
        var jana = list.Items.Single(i => i.IdentityKey == "email:jana@example.com");
        Assert.True(JsonNode.DeepEquals(new JsonObject
            {
                ["name"] = "Jana Nováková", ["emails"] = new JsonArray("jana@example.com"), ["phones"] = new JsonArray("+420 111 222 333"),
                ["objectId"] = directory.Id.ToString(),
            },
            jana.Attributes), jana.Attributes.ToJsonString());
        Assert.Equal(new[] { "atlas.contact", "atlas.page" }, jana.Sources.Select(s => s.ResourceType));
        Assert.DoesNotContain("999", ContactExport.Csv(list));
        Assert.DoesNotContain("999", ContactExport.VCard(list));
        var petr = list.Items.Single(i => i.IdentityKey == "email:petr@example.com");
        Assert.Equal("+420 111 000 001,+420 111 000 002", string.Join(',', petr.Attributes["phones"]!.AsArray().Select(x => x!.GetValue<string>()).Order()));
    }

    [PostgreSqlFact]
    public async Task A_private_contact_is_its_owners_and_its_grantees_while_the_grant_lasts_and_no_administrators()
    {
        await using var f = await RelationsFixture.CreateAsync(outbox: true);
        var contact = await f.ContactAsync(f.Writer, ObjectScopes.Private, "Soukromá", "tajna@example.com");
        await DrainAsync(f);
        Assert.Equal(1, (await ListAsync(f, f.Writer)).Total);
        Assert.Equal(0, (await ListAsync(f, f.Reader)).Total);
        Assert.Equal(0, (await ListAsync(f, f.Admin)).Total);
        Assert.Equal(0, (await ListAsync(f, f.Reader, "resource", contact)).Total);

        // A grant on the one object (AccessSql.ObjectGrantRole, the rule of ObjectScopeAccess) opens it to the grantee only.
        await f.Host.ExecuteAsync($"""INSERT INTO "ResourceGrant"("Id","ObjectId","UserId","Role","ActiveFrom") VALUES (gen_random_uuid(),:id,'{f.Reader.Id}',1,now())""", contact);
        Assert.Equal(new[] { contact }, (await ListAsync(f, f.Reader, "resource", contact)).Items.SelectMany(i => i.Sources).Select(s => s.ResourceId));
        Assert.Equal(0, (await ListAsync(f, f.Outsider)).Total);
        Assert.Equal(0, (await ListAsync(f, f.Admin)).Total);

        await f.Host.ExecuteAsync("""UPDATE "ResourceGrant" SET "ActiveTo"=now() WHERE "ObjectId"=:id""", contact);
        Assert.Equal(0, (await ListAsync(f, f.Reader)).Total);
    }

    [PostgreSqlFact]
    public async Task A_repeated_or_late_delivery_rebuilds_the_same_rows_and_the_rebuild_fills_an_emptied_catalog()
    {
        await using var f = await RelationsFixture.CreateAsync(outbox: true);
        var page = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Lidé", Rich(Card("Jana", "jana@example.com")), "x"), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        await DrainAsync(f);
        var before = await RowsAsync(f, page.Id);
        Assert.Equal("published:email:jana@example.com,working:email:jana@example.com", before);

        // Duplicate and stale deliveries: the consumer reads the current state, so nothing changes.
        await f.Host.InvokeAsync(new EntityCatalogRevision(new ContentRevised(page.Id, "atlas.page", "working", Guid.NewGuid())));
        await f.Host.InvokeAsync(new EntityCatalogRevision(new ContentRevised(page.Id, "atlas.page", "working", null)));
        Assert.Equal(before, await RowsAsync(f, page.Id));

        // After a restore the catalog is empty and the objects module's rebuild enqueues every resource with contacts.
        await f.Host.ExecuteAsync("""DELETE FROM "EntityOccurrence" WHERE "ResourceId"=:id""", page.Id);
        await f.Host.InvokeAsync<int>(new AnnounceCatalogRebuild());
        await DrainAsync(f);
        Assert.Equal(before, await RowsAsync(f, page.Id));
    }

    [PostgreSqlFact]
    public async Task A_delivery_waits_for_a_permanent_delete_holding_the_envelope_and_never_deadlocks_with_it()
    {
        await using var f = await RelationsFixture.CreateAsync(outbox: true);
        var contact = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana", "jana@example.com");
        await DrainAsync(f);
        Assert.Equal("published:email:jana@example.com", await RowsAsync(f, contact));

        // The purge's order (ObjectTrashHandler.OpenTrashedAsync, ObjectPurge.ForgetAsync): the envelope FOR UPDATE, then the occurrences.
        await using var purge = new NpgsqlConnection(f.Host.ConnectionString);
        await purge.OpenAsync();
        await using var transaction = await purge.BeginTransactionAsync();
        await Command($"""SELECT 1 FROM "ContentResource" WHERE "Id"='{contact}' FOR UPDATE""").ExecuteNonQueryAsync();
        var delivery = f.Host.InvokeAsync(new EntityCatalogRevision(new ContentRevised(contact, ContactObjectType.Id, "published", null)));
        var started = DateTime.UtcNow;
        while ((long)(await Command("SELECT count(*) FROM pg_stat_activity WHERE pg_backend_pid() = ANY(pg_blocking_pids(pid))").ExecuteScalarAsync())! == 0)
        {
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "The delivery never waited for the purge's envelope.");
            await Task.Delay(20);
        }
        // The delivery waits before it touches an occurrence, so the purge's delete goes through (it was a deadlock, PER3-2).
        Assert.Equal(1, await Command($"""DELETE FROM "EntityOccurrence" WHERE "ResourceId"='{contact}'""").ExecuteNonQueryAsync());
        await transaction.RollbackAsync();
        await delivery;
        Assert.Equal("published:email:jana@example.com", await RowsAsync(f, contact));

        NpgsqlCommand Command(string sql) => new(sql, purge, transaction);
    }

    [PostgreSqlFact]
    public async Task The_migration_enqueues_existing_pages_with_contacts_and_their_delivery_fills_the_catalog()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_entities_upgrade_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026102950);
        const string Owner = "00000000-0000-0000-0000-000000000001";
        const string Page = "00000000-0000-0000-0000-0000000000a1";
        const string Plain = "00000000-0000-0000-0000-0000000000a2";
        var content = Rich(Card("Jana", "jana@example.com")).Replace("'", "''");
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Page}','00000000-0000-0000-0000-00000000000a','{Owner}',1),('{Plain}','00000000-0000-0000-0000-00000000000a','{Owner}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              VALUES(gen_random_uuid(),'{Page}',1,'Lidé','{content}','x','{Owner}',now(),'web'),(gen_random_uuid(),'{Plain}',1,'Nic','{"{"}{"}"}','x','{Owner}',now(),'web');
            DELETE FROM "OutboxMessage";
            """);
        // Up to 2026110421, which hands the queue to the upgrade job.
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110421, from: 2026102950);
        Assert.Equal($"{Page}|atlas.page", await OutboxTests.ScalarAsync(schema.ConnectionString,
            """SELECT string_agg(("Payload"->>'resourceId') || '|' || ("Payload"->>'contentType'), ',') FROM "OutboxMessage" """));

        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled, outbox: true);
        await OutboxTests.DeliverUpgradedAsync(host);
        Assert.Equal("working:email:jana@example.com", await OutboxTests.ScalarAsync(host.ConnectionString,
            """SELECT string_agg("RevisionKind" || ':' || "IdentityKey", ',') FROM "EntityOccurrence" """));
    }

    [PostgreSqlFact]
    public async Task After_migration_2026102985_the_rebuild_announces_rebuild_not_a_publication_and_queued_messages_still_deliver()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_entities_rebuild_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026102985);
        const string Contact = "00000000-0000-0000-0000-0000000000c1";
        // An existing directory contact; its insert queued a ContentRevised 'published' as the previous release did.
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $$"""
            INSERT INTO "ContentResource"("Id","TypeId","Scope","CreatedAt","Title","Data")
              VALUES('{{Contact}}','atlas.contact','wiki',now(),'Jana','{"name":"Jana","emails":["jana@example.com"]}');
            """);
        // Up to 2026110421, which hands the queue to the upgrade job and replaces the function.
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110421, from: 2026102985);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, "SELECT atlas_enqueue_entity_catalog()");

        Assert.Equal("published,rebuild", await OutboxTests.ScalarAsync(schema.ConnectionString,
            """SELECT string_agg("Payload"->>'revisionKind', ',' ORDER BY "Id") FROM "OutboxMessage" WHERE "Payload"->>'resourceId'='00000000-0000-0000-0000-0000000000c1'"""));
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled, outbox: true);
        await OutboxTests.DeliverUpgradedAsync(host);
        Assert.Equal("published:email:jana@example.com", await OutboxTests.ScalarAsync(host.ConnectionString,
            """SELECT string_agg("RevisionKind" || ':' || "IdentityKey", ',') FROM "EntityOccurrence" """));
    }

    /// <summary>PERF2-1: a listed entity carries its first sources and their count, never every occurrence; one identity or an export carries all.</summary>
    [PostgreSqlFact]
    public async Task A_list_carries_the_first_sources_and_their_count_and_one_identity_or_an_export_all_of_them()
    {
        await using var f = await RelationsFixture.CreateAsync(outbox: true);
        var pages = new List<Guid>();
        for (var i = 1; i <= ListEntities.SourceLimit + 2; i++)
        {
            var page = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, $"Porada {i}", Rich(Card("Jana Nováková", "jana@example.com")), "x"), f.Writer, default);
            pages.Add((await f.Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default)).Id);
        }
        var petr = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Jiná", Rich(Card("Petr", "petr@example.com")), "x"), f.Writer, default);
        await f.Pages.PublishAsync(petr.Id, new PublishPageRequest(petr.Version), f.Writer, default);
        await DrainAsync(f);

        var listed = (await ListAsync(f, f.Reader)).Items.Single(x => x.IdentityKey == "email:jana@example.com");
        Assert.Equal((ListEntities.SourceLimit, ListEntities.SourceLimit + 2), (listed.Sources.Count, listed.SourceCount));
        var one = await f.As<EntityList>(f.Reader, new ListEntities(EntityKinds.Contact, "instance", null, null, Identity: "email:jana@example.com"));
        Assert.Equal(1, one.Total);
        Assert.Equal(pages.Order(), Assert.Single(one.Items).Sources.Select(x => x.ResourceId).Order());
        Assert.Equal(ListEntities.SourceLimit + 2, (await f.As<EntityList>(f.Reader, new ListEntities(EntityKinds.Contact, "instance", null, null, All: true)))
            .Items.Single(x => x.IdentityKey == "email:jana@example.com").Sources.Count);
        // The same rights: one identity the reader cannot read is no entity.
        Assert.Equal(0, (await f.As<EntityList>(f.Outsider, new ListEntities(EntityKinds.Contact, "instance", null, null, Identity: "email:jana@example.com"))).Total);
    }

    [Fact]
    public void Exports_write_plain_text_and_neutralize_formulas()
    {
        var list = new EntityList([new EntityItem("email:a@b.cz", "contact", JsonNode.Parse("""
            {"name":"**Jana** Nováková","organization":"=HYPERLINK(1)","title":"Vedoucí; provozu","emails":["a@b.cz","c@d.cz"],"address":"Ulice 1\nPraha"}
            """)!.AsObject(), [new EntitySource(Guid.Empty, "atlas.page", "Dodavatelé", null, null, "tym", "published")], 1)], 1, 1, 50);

        Assert.Equal("Jméno,Funkce,Organizace,Oddělení,E-maily,Telefony,Web,Adresa,Výskyty\r\n" +
            "\"Jana Nováková\",\"Vedoucí; provozu\",\"'=HYPERLINK(1)\",\"\",\"a@b.cz; c@d.cz\",\"\",\"\",\"Ulice 1, Praha\",\"Dodavatelé\"\r\n", ContactExport.Csv(list));
        Assert.Equal("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Jana Nováková\r\nN:Jana Nováková;;;;\r\nORG:=HYPERLINK(1)\r\nTITLE:Vedoucí\\; provozu\r\n" +
            "EMAIL;TYPE=INTERNET:a@b.cz\r\nEMAIL;TYPE=INTERNET:c@d.cz\r\nADR:;;Ulice 1\\, Praha;;;;\r\nEND:VCARD\r\n", ContactExport.VCard(list));
    }

    private static Task<EntityList> ListAsync(RelationsFixture f, UserAccount actor, string scope = "instance", Guid? id = null, string? q = null) =>
        f.As<EntityList>(actor, new ListEntities(EntityKinds.Contact, scope, id, q));

    private static Task DrainAsync(RelationsFixture f) => OutboxTests.DrainAsync(f.Host);

    private static async Task<string?> RowsAsync(RelationsFixture f, Guid resource) => (string?)await OutboxTests.ScalarAsync(f.Host.ConnectionString,
        $"""SELECT string_agg("RevisionKind" || ':' || "IdentityKey", ',' ORDER BY "RevisionKind","IdentityKey") FROM "EntityOccurrence" WHERE "ResourceId"='{resource}'""");
}
