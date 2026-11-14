using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Queries;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Relations;

/// <summary>
/// 3.6 "no logic in the database", stream S2: the page links and the contact mentions are derived by code (outbox deliveries of
/// ContentRevised and rebuild jobs) since migrations 2026110871 and 2026110872, where triggers did it. The migration test pins what the
/// triggers produced over a varied set of existing rows and shows the jobs rebuild exactly that; the others show the code's own paths.
/// </summary>
public sealed class PageDerivedRowsTests
{
    private const string Owner = "00000000-0000-0000-0000-000000000001";
    private const string SpaceA = "00000000-0000-0000-0000-00000000000a";
    private const string SpaceB = "00000000-0000-0000-0000-00000000000b";

    private static string Id(char kind, int n) => $"00000000-0000-0000-0000-{kind switch { 'p' => 'a', 'r' => 'b', _ => 'c' }}{n:D11}";

    private static string Doc(params string[] blocks) =>
        "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[" + string.Join(",", blocks) + "]}}";

    private static string Paragraph(params string[] inline) => "{\"type\":\"paragraph\",\"content\":[" + string.Join(",", inline) + "]}";

    private static string Ref(string id) => "{\"type\":\"pageReference\",\"attrs\":{\"id\":\"" + id + "\",\"title\":\"T\",\"url\":\"#/pages/" + id + "\"}}";

    private static string Link(string href) => "{\"type\":\"text\",\"text\":\"l\",\"marks\":[{\"type\":\"link\",\"attrs\":{\"href\":\"" + href + "\"}}]}";

    private static string Card(params string[] contacts) =>
        "{\"type\":\"contactCard\",\"attrs\":{\"data\":{\"version\":1,\"contacts\":[" + string.Join(",", contacts) + "]}}}";

    private static string Person(string? email = null, string? objectId = null) =>
        "{\"name\":\"X\"" + (email is null ? "" : $",\"emails\":[\"{email}\"]") + (objectId is null ? "" : $",\"objectId\":\"{objectId}\"") + "}";

    private static string Revision(string id, string page, int version, string content, bool open) => $"""
        INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel")
          VALUES('{id}','{page}',{version},'P','{content.Replace("'", "''")}','','{Owner}',now()-interval '{(open ? 10 : 30)} minutes',{(open ? "NULL" : "now()-interval '20 minutes'")},'web');
        """;

    private static string Page(string id, string space) => $"""INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{id}','{space}','{Owner}',1);""";

    /// <summary>The existing rows of a wiki that has pages with links of every shape and mentions of every kind, built while the
    /// triggers still derive them.</summary>
    private static string Seed(Guid missing) => $$"""
        INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{{Owner}}','writer','Writer',false,true,'stamp');
        INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{{SpaceA}}','First','book-open','blue'),('{{SpaceB}}','Second','book-open','blue');
        INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","CreatedAt","Title","Data","TrashedAt") VALUES
          ('{{Id('c', 1)}}','atlas.contact','wiki',NULL,now(),'A','{"name":"A","emails":["a@x.cz"]}',NULL),
          ('{{Id('c', 2)}}','atlas.contact','space','{{SpaceA}}',now(),'B','{"name":"B","emails":["b@x.cz"]}',NULL),
          ('{{Id('c', 3)}}','atlas.contact','space','{{SpaceB}}',now(),'B2','{"name":"B2","emails":["b@x.cz"]}',NULL),
          ('{{Id('c', 4)}}','atlas.contact','wiki',NULL,now(),'D1','{"name":"D1","emails":["d@x.cz"]}',NULL),
          ('{{Id('c', 5)}}','atlas.contact','wiki',NULL,now(),'D2','{"name":"D2","emails":["d@x.cz"]}',NULL),
          ('{{Id('c', 6)}}','atlas.contact','wiki',NULL,now(),'E','{"name":"E","emails":["e@x.cz"]}',now());
        {{Page(Id('p', 1), SpaceA)}} {{Page(Id('p', 2), SpaceA)}} {{Page(Id('p', 3), SpaceA)}} {{Page(Id('p', 4), SpaceB)}} {{Page(Id('p', 5), SpaceA)}}
        {{Page(Id('p', 6), SpaceA)}}
        -- p1: published links (a reference, relative and absolute hrefs, an upper-case id, a self link, a bad id) and mentions; its
        -- working revision links and mentions otherwise.
        {{Revision(Id('r', 1), Id('p', 1), 1, Doc(
            Paragraph(Ref(Id('p', 2)), Link($"#/pages/{Id('p', 3)}"), Link($"https://wiki.example/#/pages/{Id('p', 4).ToUpperInvariant()}?heading=h"), Link($"/#/pages/{Id('p', 5)}/x"),
                Link($"#/pages/{Id('p', 1)}"), Link("#/pages/not-a-uuid"), Ref("{" + Id('p', 7) + "}"), Ref(Id('p', 7) + " "), Ref(Id('p', 7).Replace("-", "")), Link($"#/pagesx/{Id('p', 7)}"), Ref(Id('p', 1))),
            "{\"type\":\"bulletList\",\"content\":[{\"type\":\"listItem\",\"content\":[" + Card(Person(objectId: Id('c', 1).ToUpperInvariant()), Person("A@X.cz"),
                Person("b@x.cz"), Person("d@x.cz"), Person("e@x.cz"), Person("nobody@x.cz"), Person(objectId: Id('p', 2)), Person(objectId: missing.ToString())) + "]}]}",
            "{\"type\":\"embed\",\"attrs\":{\"href\":\"#/pages/" + Id('p', 6) + "\"}}"), open: false)}}
        {{Revision(Id('r', 2), Id('p', 1), 2, Doc(Paragraph(Ref(Id('p', 3))), Card(Person(objectId: Id('c', 4)), Person("a@x.cz", "not-a-uuid"))), open: true)}}
        UPDATE "Page" SET "PublishedRevisionId"='{{Id('r', 1)}}' WHERE "Id"='{{Id('p', 1)}}';
        -- p2: published only, links back to p1.
        {{Revision(Id('r', 3), Id('p', 2), 1, Doc(Paragraph(Ref(Id('p', 1))), Card(Person("b@x.cz"))), open: true)}}
        UPDATE "Page" SET "PublishedRevisionId"='{{Id('r', 3)}}' WHERE "Id"='{{Id('p', 2)}}';
        -- p3: never published; p4: published and in the trash, in the other space; p5: published, then moved to the other space.
        {{Revision(Id('r', 4), Id('p', 3), 1, Doc(Paragraph(Ref(Id('p', 1))), Card(Person(objectId: Id('c', 1)))), open: true)}}
        {{Revision(Id('r', 5), Id('p', 4), 1, Doc(Paragraph(Ref(Id('p', 2))), Card(Person("b@x.cz"))), open: true)}}
        UPDATE "Page" SET "PublishedRevisionId"='{{Id('r', 5)}}',"IsTrashed"=true,"TrashedAt"=now(),"TrashedById"="OwnerId","TrashBatchId"=gen_random_uuid() WHERE "Id"='{{Id('p', 4)}}';
        {{Revision(Id('r', 6), Id('p', 5), 1, Doc(Paragraph(Ref(Id('p', 1))), Card(Person("b@x.cz"))), open: true)}}
        UPDATE "Page" SET "PublishedRevisionId"='{{Id('r', 6)}}' WHERE "Id"='{{Id('p', 5)}}';
        UPDATE "Page" SET "SpaceId"='{{SpaceB}}' WHERE "Id"='{{Id('p', 5)}}';
        -- p6: content that is not what the editor stores.
        {{Revision(Id('r', 7), Id('p', 6), 1, "{}", open: true)}}
        UPDATE "Page" SET "PublishedRevisionId"='{{Id('r', 7)}}' WHERE "Id"='{{Id('p', 6)}}';
        """;

    private const string Links = """SELECT coalesce(string_agg("SourcePageId" || '>' || "TargetPageId" || '@' || "SourceRevisionId", E'\n' ORDER BY "SourcePageId", "TargetPageId"), '') FROM "PageLink" """;

    private const string Mentions = """SELECT coalesce(string_agg("SourcePageId" || '>' || "ObjectId" || '@' || "SourceRevisionId", E'\n' ORDER BY "SourcePageId", "ObjectId", "SourceRevisionId"), '') FROM "PageObjectMention" """;

    [PostgreSqlFact]
    public async Task The_rebuild_jobs_make_the_rows_the_triggers_made_over_existing_pages_and_a_second_run_changes_nothing()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_page_derived_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110871);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, Seed(Guid.NewGuid()));
        var (links, mentions) = ((string)(await OutboxTests.ScalarAsync(schema.ConnectionString, Links))!, (string)(await OutboxTests.ScalarAsync(schema.ConnectionString, Mentions))!);
        // The pinned rows are not empty and have every shape: p1's links (p2 by reference, p3, p4, p5; none to itself or to a bad id),
        // p2's link back, p5's mention of the contact of its new space.
        Assert.Equal(8, links.Split('\n').Length);
        Assert.Contains($"{Id('p', 1)}>{Id('p', 4)}@{Id('r', 1)}", links);
        Assert.DoesNotContain($"{Id('p', 1)}>{Id('p', 1)}", links);
        Assert.Contains($"{Id('p', 1)}>{Id('p', 6)}@{Id('r', 1)}", links);
        Assert.DoesNotContain($"{Id('p', 1)}>{Id('p', 7)}", links);
        Assert.Contains($"{Id('p', 1)}>{Id('c', 1)}@{Id('r', 1)}", mentions);
        Assert.DoesNotContain(Id('c', 5), mentions);
        Assert.DoesNotContain(Id('c', 6), mentions);
        Assert.Contains($"{Id('p', 5)}>{Id('c', 3)}@{Id('r', 6)}", mentions);
        Assert.Contains($"{Id('p', 1)}>{Id('c', 4)}@{Id('r', 2)}", mentions);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        // The triggers and the functions are gone, the rows stayed and the rebuilds are queued.
        Assert.Equal(0L, await OutboxTests.ScalarAsync(schema.ConnectionString, """
            SELECT (SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal AND tgname IN ('TR_Page_PageLink','TR_Page_PageObjectMention','TR_Page_PageObjectMention_Space',
              'TR_PageRevision_PageObjectMention','TR_PageRevision_PageObjectMention_Content'))
              + (SELECT count(*) FROM pg_proc WHERE proname IN ('atlas_page_link_targets','atlas_refresh_page_links','atlas_rebuild_page_links','atlas_page_links_trigger',
              'atlas_page_object_mentions','atlas_refresh_page_object_mentions','atlas_rebuild_page_object_mentions','atlas_page_object_mentions_trigger',
              'atlas_page_revision_object_mentions_trigger'))
            """));
        Assert.Equal((links, mentions), ((string)(await OutboxTests.ScalarAsync(schema.ConnectionString, Links))!, (string)(await OutboxTests.ScalarAsync(schema.ConnectionString, Mentions))!));
        Assert.Equal(2L, await OutboxTests.ScalarAsync(schema.ConnectionString, """
            SELECT count(*) FROM "JobRun" WHERE "JobId" IN ('pages.links-rebuild','contacts.mentions-rebuild') AND "State" = 'queued'
            """));

        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        var runner = host.Services.GetRequiredService<IJobRunner>();
        // Rows gone and rows wrong are both made right.
        await OutboxTests.ExecuteAsync(host.ConnectionString, $"""
            DELETE FROM "PageLink";
            DELETE FROM "PageObjectMention";
            INSERT INTO "PageLink" VALUES ('{Id('p', 6)}', '{Id('p', 1)}', '{Id('r', 7)}');
            INSERT INTO "PageObjectMention" VALUES ('{Id('p', 6)}', '{Id('c', 1)}', '{Id('r', 7)}');
            """);
        await runner.RunToEndAsync(PageLinkRebuildJob.JobId, "Test");
        await runner.RunToEndAsync(PageObjectMentionRebuildJob.JobId, "Test");
        Assert.Equal((links, mentions), ((string)(await OutboxTests.ScalarAsync(host.ConnectionString, Links))!, (string)(await OutboxTests.ScalarAsync(host.ConnectionString, Mentions))!));

        await runner.RunToEndAsync(PageLinkRebuildJob.JobId, "Test");
        await runner.RunToEndAsync(PageObjectMentionRebuildJob.JobId, "Test");
        Assert.Equal((links, mentions), ((string)(await OutboxTests.ScalarAsync(host.ConnectionString, Links))!, (string)(await OutboxTests.ScalarAsync(host.ConnectionString, Mentions))!));
    }

    /// <summary>A delivery of the page's announcement derives its rows: a save (the working revision's mentions), a publication (links and
    /// mentions) and a move into another space (the snapshot without an id matches by e-mail there); a repeated delivery changes nothing.</summary>
    [PostgreSqlFact]
    public async Task A_page_announcement_derives_its_links_and_mentions_and_a_move_matches_by_address_again()
    {
        await using var f = await RelationsFixture.CreateAsync(outbox: true); // own host: DeriveAsync waits for the whole envelope table (OutboxTests.DrainAsync)
        var other = await f.Host.SeedSpaceAsync("Jiný", (f.Writer, SpaceRole.Writer));
        var here = await f.ContactAsync(f.Writer, ObjectScopes.Space, "Tady", "same@example.com");
        // The directory refuses a second contact with the address, so the other space's is written as an old row.
        var there = Guid.NewGuid();
        await OutboxTests.ExecuteAsync(f.Host.ConnectionString, $$"""
            INSERT INTO "ContentResource"("Id","TypeId","SpaceId","CreatedAt","Scope","Version","CreatedBy","UpdatedAt","Title","Data")
            VALUES('{{there}}','atlas.contact','{{other}}',now(),'space',1,'{{f.Writer.Id}}',now(),'Tam','{"name":"Tam","emails":["same@example.com"]}')
            """);
        var target = await f.PageAsync("Cíl");
        var page = await f.PageAsync("Zdroj", null, false, RelationsFixture.PageRef(target.Id), RelationsFixture.ContactInline("same@example.com"));

        await f.DeriveAsync();
        // The working revision's mention counts at once; the link waits for the publication.
        Assert.Equal(1L, await Count(f, "PageObjectMention", page.Id, here));
        Assert.Equal(0L, await Count(f, "PageLink", page.Id, target.Id));
        var published = await f.Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        await f.DeriveAsync();
        Assert.Equal(1L, await Count(f, "PageLink", page.Id, target.Id));
        Assert.Equal(1L, await Count(f, "PageObjectMention", page.Id, here));

        var tree = await f.As<PageTreeDto>(f.Admin, new GetPageTree(f.SpaceId));
        await f.As<CrossSpaceChange>(f.Admin, new MovePageToSpace(page.Id, new MovePageSpaceRequest(other, null, null, true, tree.Version,
            (await f.As<PageTreeDto>(f.Admin, new GetPageTree(other))).Version, published.Version, true)));
        await f.DeriveAsync();
        Assert.Equal((0L, 1L), (await Count(f, "PageObjectMention", page.Id, here), await Count(f, "PageObjectMention", page.Id, there)));
    }

    /// <summary>The last-usage warning of a contact counts a mention saved a moment ago, before any delivery of the save ran.</summary>
    [PostgreSqlFact]
    public async Task The_last_usage_count_of_a_contact_sees_a_mention_saved_before_its_delivery_ran()
    {
        await using var f = await RelationsFixture.SharedAsync();
        // The directory refuses a second contact with an address anywhere in the wiki: one of this test's own.
        var contact = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana", $"jana@{Guid.NewGuid():N}.example.com");
        var inline = """{"type":"contactInline","attrs":{"data":{"version":1,"contacts":[{"name":"Jana","objectId":"CONTACT"}]}}}""".Replace("CONTACT", contact.ToString());
        // Written through the page handlers, not through the fixture's As (which would derive first): no delivery has run.
        var page = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Lidé", RelationsFixture.Rich(inline), "Lidé"), f.Writer, default);
        Assert.Equal(0L, await OutboxTests.ScalarAsync(f.Host.ConnectionString, $"""SELECT count(*) FROM "PageObjectMention" WHERE "ObjectId" = '{contact}'"""));

        // The badges' count (asked per candidate in the editor) neither scans the revisions nor writes a row.
        using (var recording = SqlRecording.Start())
        {
            Assert.Equal(new UsageCount(contact, 0, 0, 0), await f.Boards.As<UsageCount>(f.Writer, new CountObjectUsage(contact)));
            Assert.DoesNotContain(recording.Statements, sql => sql.Contains("strpos", StringComparison.Ordinal)
                || sql.Contains("INSERT INTO \"PageObjectMention\"", StringComparison.Ordinal) || sql.Contains("DELETE FROM \"PageObjectMention\"", StringComparison.Ordinal));
        }

        // The last-usage warning of a removal or delete derives the pages that may be behind first.
        Assert.Equal(new UsageCount(contact, 1, 1, 0), await f.Boards.As<UsageCount>(f.Writer, new CountObjectUsage(contact, Fresh: true)));

        // The mention removed in the next save is forgotten the same way: the row of a revision no longer current does not count.
        var saved = await f.Pages.SaveAsync(page.Id, new SavePageRequest("Lidé", RelationsFixture.Rich(), "Lidé", page.Version), f.Writer, default);
        Assert.Equal(new UsageCount(contact, 0, 0, 0), await f.Boards.As<UsageCount>(f.Writer, new CountObjectUsage(contact, Fresh: true)));
        Assert.NotEqual(page.Version, saved.Version);
    }

    /// <summary>The editor stores documents up to 136 levels deep; the default JSON depth of 64 would have dropped every link and mention.</summary>
    [Fact]
    public void Links_and_mentions_are_found_in_a_deeply_nested_document()
    {
        var target = Guid.NewGuid();
        var contact = Guid.NewGuid();
        var inner = Paragraph(Ref(target.ToString())) + "," + Card(Person(objectId: contact.ToString()));
        for (var level = 0; level < 30; level++)
        {
            inner = "{\"type\":\"blockquote\",\"content\":[" + inner + "]}";
        }
        Assert.Equal([target], PageLinkTargets.Of(Doc(inner)));
        Assert.Equal([contact], PageObjectMentions.Snapshots(Doc(inner)).Select(x => x.Named!.Value));
    }

    /// <summary>A template toggle and a move into another space announce a silent rebuild, which the delivery of each consumer takes.</summary>
    [PostgreSqlFact]
    public async Task A_template_toggle_and_a_move_announce_a_silent_rebuild()
    {
        await using var f = await RelationsFixture.CreateAsync(outbox: true); // own host: DeriveAsync waits for the whole envelope table (OutboxTests.DrainAsync)
        var other = await f.Host.SeedSpaceAsync("Jiný", (f.Writer, SpaceRole.Writer));
        var page = await f.PageAsync("Stránka");
        await f.DeriveAsync();

        var toggled = await f.Host.TrackAsync(() => f.As<PageDto>(f.Admin, new Atlas.Server.Features.PageTemplates.SetPageTemplate(page.Id, true)));
        Assert.Contains(toggled.Executed.MessagesOf<PageLinkRevision>(), delivery => delivery.Event.RevisionKind == "rebuild" && delivery.Event.ResourceId == page.Id);

        var tree = await f.As<PageTreeDto>(f.Admin, new GetPageTree(f.SpaceId));
        var targetTree = await f.As<PageTreeDto>(f.Admin, new GetPageTree(other));
        var moved = await f.Host.TrackAsync(() => f.As<CrossSpaceChange>(f.Admin, new MovePageToSpace(page.Id,
            new MovePageSpaceRequest(other, null, null, true, tree.Version, targetTree.Version, page.Version, true))));
        Assert.Contains(moved.Executed.MessagesOf<PageObjectMentionRevision>(), delivery => delivery.Event.RevisionKind == "rebuild" && delivery.Event.ResourceId == page.Id);
    }

    [Fact]
    public void A_link_is_a_page_reference_or_any_href_holding_a_page_address_and_nothing_else()
    {
        var id = Guid.NewGuid();
        Guid[] Of(string document) => [.. PageLinkTargets.Of(Doc(document))];
        Assert.Equal([id], Of(Paragraph(Ref(id.ToString().ToUpperInvariant()), Link($"#/pages/{id}"), Link($"https://x.cz/#/pages/{id}?heading=a"))));
        Assert.Empty(Of(Paragraph(Link($"#/pages/{id}x"), Link($"#/page/{id}"), Link("#/pages/12345"), Ref("not-a-uuid"))));
        // Markdown stores atlas://pages/ as #/pages/ and the editor's links take no atlas: scheme, so it is no stored form.
        Assert.Empty(Of(Paragraph(Link($"atlas://pages/{id}"))));
        Assert.Empty(PageLinkTargets.Of(Doc(Paragraph(Link($"#/pages/{id}"))), hrefs: false));
        Assert.Empty(PageLinkTargets.Of("{}"));
        Assert.Empty(PageLinkTargets.Of("not json"));
    }

    private static async Task<long> Count(RelationsFixture f, string table, Guid source, Guid target)
        => (long)(await OutboxTests.ScalarAsync(f.Host.ConnectionString,
            $"""SELECT count(*) FROM "{table}" WHERE "SourcePageId" = '{source}' AND "{(table == "PageLink" ? "TargetPageId" : "ObjectId")}" = '{target}'"""))!;
}
