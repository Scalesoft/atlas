using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Linking.Commands;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Linking;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Linking;

/// <summary>
/// P15 „Provázání samo od sebe“ on the server (audit engine §5–§6, owner decisions R1 and R2): the graph of what content names
/// (ContentReference, migration 2026110310) from pages, comments and board items; the overview „Nepropojené zmínky“ with a cursor; one
/// mention linked or kept as text (only the page says what is linked: nothing is remembered beside it); a page linked whole; and the link-only rule: a change that only turns text into chips goes
/// straight into the published version (undoable from history), anything else stays a draft.
/// </summary>
public sealed class ContentReferenceTests
{
    [PostgreSqlFact]
    public async Task Pages_comments_and_board_text_are_extracted_and_an_agent_links_a_page_straight_into_the_published_version()
    {
        await using var f = await SiteAsync();
        var space = await HomeAsync(f);
        var petr = await ContactAsync(f, space, "Petr Svoboda", "Modelsoft");
        var janaA = await ContactAsync(f, space, "Jana Nováková", "Modelsoft");
        var janaB = await ContactAsync(f, space, "Jana Nováková", "Example");
        await PageAsync(f.Admin, space, "Provozní příručka", Rich(Paragraph("Postupy.")), "Postupy.");
        var page = await PageAsync(f.Admin, space, "Tým", Rich(Heading(2, "lide", "Lidé"), Paragraph("Volal Petr Svoboda a pak Jana Nováková."),
            Paragraph("Napiš Petru Svobodovi."), Paragraph("Viz Provozní příručka.")), "x");
        Assert.Equal(HttpStatusCode.Created, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/objects/{page.Id}/comments", new { body = "Petr Svoboda to ví." })).StatusCode);
        var board = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/spaces/{space}/boards", new { title = "Plán" }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        await Expect(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/boards/{board}/operations", new
        {
            operationId = Guid.NewGuid(),
            changes = new object[] { new { type = "create", item = new { id = Guid.NewGuid(), kind = "note", x = 0, y = 0, width = 160, height = 120, z = 0, color = "yellow", text = "Zavolat Jana Nováková", props = new { } } } },
        }), HttpStatusCode.OK);
        await SweepAsync(f);

        Assert.Equal("0:person:suggested:Petr Svoboda:person.full|1:person:suggested:Jana Nováková:person.full|2:person:suggested:Petru Svobodovi:person.stem|" +
            "3:page:suggested:Provozní příručka:page.full", await RowsAsync(f, page.Id, "rev:"));
        Assert.Equal("0:person:suggested:Petr Svoboda:person.full", await RowsAsync(f, page.Id, "comment:"));
        Assert.Equal("0:person:suggested:Jana Nováková:person.full", await RowsAsync(f, board, "item:"));
        Assert.Equal(petr + "," + string.Join(",", new[] { janaA, janaB }.Order()), await f.Site.ScalarAsync("""
            SELECT string_agg(c, ',' ORDER BY r."Ordinal", c) FROM "ContentReference" r, unnest(r."Candidates") c WHERE r."SourceId" = @page AND r."SourcePart" LIKE 'rev:%' AND r."Ordinal" < 2
            """, ("page", Guid.Parse(page.Id))) is string keys ? keys.Replace("object:", "") : null);

        // The overview: the page's mentions with the people it may be. Plain text kept as text changes nothing and is offered again:
        // the page alone says what is linked (owner decision), and the old "dismiss" is gone.
        var list = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/spaces/{space}/unlinked"), HttpStatusCode.OK);
        Assert.Equal(["Petr Svoboda", "Jana Nováková", "Petru Svobodovi", "Provozní příručka"], Texts(list));
        var jana = list["items"]![1]!;
        Assert.Equal(("Tým", 1, "lide", "person", "suggested", page.Version), (jana["pageTitle"]!.GetValue<string>(), jana["ordinal"]!.GetValue<int>(),
            jana["anchor"]!.GetValue<string>(), jana["kind"]!.GetValue<string>(), jana["state"]!.GetValue<string>(), jana["version"]!.GetValue<int>()));
        Assert.Equal(["Example", "Modelsoft"], jana["candidates"]!.AsArray().Select(x => x!["hint"]!.GetValue<string>()).Order());
        Assert.Equal("Volal Petr Svoboda a pak Jana Nováková.", jana["context"]!.GetValue<string>());
        Assert.Equal(4, (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/spaces/{space}/unlinked?pageId={page.Id}"), HttpStatusCode.OK))["items"]!.AsArray().Count);
        var kept = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/pages/{page.Id}/references",
            new { expectedVersion = page.Version, ordinal = 3, text = "Provozní příručka", choice = "text" }), HttpStatusCode.OK);
        Assert.Equal((false, page.Version), (kept["changed"]!.GetValue<bool>(), kept["version"]!.GetValue<int>()));
        Assert.Equal(["Petr Svoboda", "Jana Nováková", "Petru Svobodovi", "Provozní příručka"], Texts(await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/spaces/{space}/unlinked"), HttpStatusCode.OK)));
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/pages/{page.Id}/references",
            new { expectedVersion = page.Version, ordinal = 3, text = "Provozní příručka", choice = "dismiss" })).StatusCode);

        // An agent links the page: one Petr a chip, two Janas an unresolved chip, the inflected name only offered; link-only, so published.
        var agent = await f.ClientAsync(await f.AdminTokenAsync("read", "write"));
        var linked = Ok(await CallAsync(agent, "atlas_link_page", new { pageId = page.Id, expectedVersion = page.Version }));
        Assert.True(linked["published"]!.GetValue<bool>());
        Assert.Equal(["Petr Svoboda"], linked["links"]!["linked"]!.AsArray().Select(x => x!["label"]!.GetValue<string>()));
        Assert.Equal(["Jana Nováková"], linked["links"]!["unresolved"]!.AsArray().Select(x => x!["text"]!.GetValue<string>()));
        Assert.Equal([("Petru Svobodovi", 2), ("Provozní příručka", 3)], linked["links"]!["suggested"]!.AsArray().Select(x => (x!["text"]!.GetValue<string>(), x["ordinal"]!.GetValue<int>())));
        var version = linked["version"]!.GetValue<int>();
        Assert.Equal(version, linked["publishedVersion"]!.GetValue<int>());
        Assert.Equal("mcp:Propojeno přes MCP", await f.Site.ScalarAsync("""
            SELECT r."Channel" || ':' || split_part(u."ChangeSummary", ':', 1) FROM "Page" p JOIN "PageRevision" r ON r."Id" = p."PublishedRevisionId"
            JOIN "PagePublication" u ON u."PageId" = p."Id" AND u."ActiveTo" IS NULL WHERE p."Id" = @page
            """, ("page", Guid.Parse(page.Id))));
        var published = await PublishedAsync(f, page.Id);
        Assert.Contains(petr, published, StringComparison.Ordinal);
        Assert.Contains("\"unresolved\":true", published, StringComparison.Ordinal);
        // The text is the same: the visible words of the published version did not change.
        Assert.Contains("Volal Petr Svoboda a pak Jana Nováková.", System.Text.RegularExpressions.Regex.Replace(await f.Site.ScalarAsync("""
            SELECT r."Markdown" FROM "Page" p JOIN "PageRevision" r ON r."Id" = p."PublishedRevisionId" WHERE p."Id" = @page
            """, ("page", Guid.Parse(page.Id))) as string ?? "", @"\[([^\]]+)\]\(a-contact:[^)]*\)", "$1"), StringComparison.Ordinal);

        // Idempotent: a second run links nothing twice and writes nothing.
        var again = Ok(await CallAsync(agent, "atlas_link_page", new { pageId = page.Id, expectedVersion = version }));
        Assert.Equal((false, version), (again["changed"]!.GetValue<bool>(), again["version"]!.GetValue<int>()));

        // One mention decided: the text must still be there, the pick is published, the same pick again is nothing.
        Assert.Equal("reference_changed", ErrorCode(await CallAsync(agent, "atlas_resolve_reference", new { pageId = page.Id, expectedVersion = version, ordinal = 2, text = "Petra", targetId = petr })));
        var resolved = Ok(await CallAsync(agent, "atlas_resolve_reference", new { pageId = page.Id, expectedVersion = version, ordinal = 2, text = "Petru Svobodovi", targetId = petr }));
        Assert.True(resolved["published"]!.GetValue<bool>());
        version = resolved["version"]!.GetValue<int>();
        Assert.False(Ok(await CallAsync(agent, "atlas_resolve_reference", new { pageId = page.Id, expectedVersion = version, ordinal = 2, text = "Petru Svobodovi", targetId = petr }))["changed"]!.GetValue<bool>());

        // What remains: the unresolved Jana with both candidates, and the page title kept as text, offered again.
        var recognized = Ok(await CallAsync(agent, "atlas_recognize", new { pageId = page.Id }));
        Assert.Equal(["Jana Nováková", "Provozní příručka"], recognized["hits"]!.AsArray().Select(x => x!["text"]!.GetValue<string>()));
        var hit = recognized["hits"]![0]!;
        Assert.Equal(("Jana Nováková", 1, "unresolved", 2), (hit["text"]!.GetValue<string>(), hit["ordinal"]!.GetValue<int>(), hit["state"]!.GetValue<string>(), hit["candidates"]!.AsArray().Count));

        // A mixed change (new text) stays a draft, linked by link auto; a pick while that draft is pending stays in the draft too.
        var inserted = Ok(await CallAsync(agent, "atlas_insert_section", new { pageId = page.Id, expectedVersion = version, heading = "Nové", level = 2, markdown = "Pak volal Petr Svoboda." }));
        Assert.Equal(["Petr Svoboda"], inserted["links"]!["linked"]!.AsArray().Select(x => x!["label"]!.GetValue<string>()));
        Assert.Equal(version, (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/pages/{page.Id}"), HttpStatusCode.OK))["publishedVersion"]!.GetValue<int>());
        var pick = Ok(await CallAsync(agent, "atlas_resolve_reference", new { pageId = page.Id, expectedVersion = inserted["version"]!.GetValue<int>(), ordinal = 1, text = "Jana Nováková", targetId = janaA }));
        Assert.Equal((true, false, version), (pick["changed"]!.GetValue<bool>(), pick["published"]!.GetValue<bool>(), pick["publishedVersion"]!.GetValue<int>()));
    }

    // Seen in the 3.2 verification: „Poznámka 3: …“ on the page Poznámka 3 was offered as a link, and linked, to itself.
    [PostgreSqlFact]
    public async Task A_page_is_not_offered_as_a_link_to_itself()
    {
        await using var f = await SiteAsync();
        var space = await HomeAsync(f);
        var manual = await PageAsync(f.Admin, space, "Provozní příručka", Rich(Paragraph("Provozní příručka popisuje postupy.")), "x");
        var team = await PageAsync(f.Admin, space, "Tým", Rich(Paragraph("Viz Provozní příručka.")), "x");
        await SweepAsync(f);

        var list = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/spaces/{space}/unlinked"), HttpStatusCode.OK);
        Assert.Equal([team.Id], list["items"]!.AsArray().Select(x => x!["pageId"]!.GetValue<string>()));
        // The mention on the page itself has no ordinal to resolve either.
        Assert.Equal(HttpStatusCode.Conflict, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/pages/{manual.Id}/references",
            new { expectedVersion = manual.Version, ordinal = 0, text = "Provozní příručka", choice = new { targetId = manual.Id } })).StatusCode);
    }

    [PostgreSqlFact]
    public async Task The_overview_shows_only_what_the_reader_may_read_pages_with_a_cursor_and_hides_a_space_out_of_ai_from_tokens()
    {
        await using var f = await SiteAsync();
        var space = await HomeAsync(f);
        var petr = await ContactAsync(f, space, "Petr Svoboda", null);
        var (reader, readerId) = await f.UserAsync("ctenar");
        await f.GrantAsync(space, readerId, "Reader");
        for (var i = 0; i < 3; i++)
        {
            await PageAsync(f.Admin, space, $"Zápis {i}", Rich(Paragraph($"Volal Petr Svoboda, bod {i}.")), "x");
        }
        // A page nobody published: its writers see its mention, its readers nothing.
        await PageAsync(f.Admin, space, "Koncept", Rich(Paragraph("Koncept pro Petr Svoboda.")), "x", publish: false);
        var other = await f.SpaceAsync("Jinde");
        await ContactAsync(f, other, "Petr Svoboda", null);
        await PageAsync(f.Admin, other, "Mimo", Rich(Paragraph("Tady volal Petr Svoboda.")), "x");
        await SweepAsync(f);

        var all = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/spaces/{space}/unlinked?limit=200"), HttpStatusCode.OK);
        Assert.Equal(4, all["items"]!.AsArray().Count);
        Assert.Null(all["nextCursor"]);
        Assert.Equal(petr, all["items"]![0]!["candidates"]![0]!["id"]!.GetValue<string>());
        var one = all["items"]![0]!["pageId"]!.GetValue<string>();
        Assert.Equal([one], (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/spaces/{space}/unlinked?pageId={one}"), HttpStatusCode.OK))["items"]!.AsArray()
            .Select(x => x!["pageId"]!.GetValue<string>()));
        var walked = new List<string>();
        string? cursor = null;
        do
        {
            var window = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/spaces/{space}/unlinked?limit=1" + (cursor is null ? "" : "&cursor=" + cursor)), HttpStatusCode.OK);
            walked.AddRange(window["items"]!.AsArray().Select(x => x!["pageId"]!.GetValue<string>() + x["ordinal"]));
            cursor = window["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null && walked.Count < 10);
        Assert.Equal(all["items"]!.AsArray().Select(x => x!["pageId"]!.GetValue<string>() + x["ordinal"]), walked);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Admin.GetAsync($"/api/spaces/{space}/unlinked?cursor=nonsense")).StatusCode);

        var readable = (await AtlasHttpSite.Json(await reader.GetAsync($"/api/spaces/{space}/unlinked"), HttpStatusCode.OK))["items"]!.AsArray()
            .Select(x => x!["pageId"]!.GetValue<string>()).ToHashSet();
        Assert.Equal(3, readable.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/spaces/{other}/unlinked")).StatusCode);

        // The web links a page whole too: link-only, so published at once. (A published page: the list goes by page ID, and the
        // unpublished one would stay a draft.)
        var first = all["items"]!.AsArray().First(x => readable.Contains(x!["pageId"]!.GetValue<string>()))!;
        var web = await Expect(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/pages/{first["pageId"]}/link",
            new { expectedVersion = first["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        Assert.True(web["published"]!.GetValue<bool>());
        Assert.Equal(2, (await AtlasHttpSite.Json(await reader.GetAsync($"/api/spaces/{space}/unlinked"), HttpStatusCode.OK))["items"]!.AsArray().Count);

        // The personal API and MCP: the same list, recognition and link auto on a write; a space out of AI is missing to them, not to the web.
        await f.SetLevelAsync(f.AdminId, "write");
        var api = f.Site.Client();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["rest"], "Linker")).Secret);
        Assert.Single((await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/spaces/{other}/unlinked"), HttpStatusCode.OK))["items"]!.AsArray());
        var recognized = await AtlasHttpSite.Json(await api.PostAsJsonAsync("/api/v1/recognize", new { spaceId = space, text = "Volal Petr Svoboda, pak Petru Svobodovi." }), HttpStatusCode.OK);
        Assert.Equal(["Petr Svoboda:chip", "Petru Svobodovi:suggest"], recognized["hits"]!.AsArray().Select(x => x!["text"]!.GetValue<string>() + ":" + x["decision"]!.GetValue<string>()));
        var created = await Expect(await api.PostAsJsonAsync("/api/v1/pages", new { spaceId = space, title = "Nový", markdown = "Volal Petr Svoboda." }), HttpStatusCode.Created);
        Assert.Equal(petr, created["links"]!["linked"]![0]!["targetId"]!.GetValue<string>());
        var plain = await AtlasHttpSite.Json(await api.PostAsJsonAsync("/api/v1/pages", new { spaceId = space, title = "Bez", markdown = "Volal Petr Svoboda.", link = "off" }), HttpStatusCode.Created);
        Assert.Empty(plain["links"]!["linked"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/v1/pages", new { spaceId = space, title = "Špatně", markdown = "x", link = "maybe" })).StatusCode);

        await f.ExcludeFromAiAsync(other);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/v1/spaces/{other}/unlinked")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.PostAsJsonAsync("/api/v1/recognize", new { spaceId = other, text = "Petr Svoboda" })).StatusCode);
        var agent = await f.ClientAsync(await f.AdminTokenAsync("read"));
        Assert.Equal("not_found", ErrorCode(await CallAsync(agent, "atlas_list_unlinked", new { spaceId = other })));
        Assert.Single((await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/spaces/{other}/unlinked"), HttpStatusCode.OK))["items"]!.AsArray());
    }

    [PostgreSqlFact]
    public async Task After_migration_2026110310_the_sweep_extracts_the_existing_pages_once()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_content_reference_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110310);
        const string Owner = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a";
        const string Page = "00000000-0000-0000-0000-0000000000a1", Revision = "00000000-0000-0000-0000-0000000000b1", Petr = "00000000-0000-0000-0000-0000000000c1";
        var content = Rich(Paragraph("Volal Petr Svoboda.")).Replace("'", "''");
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','writer','Writer',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","Title","Data") VALUES
              ('{Petr}','atlas.contact','wiki',NULL,NULL,1,now(),'{Owner}',now(),'Petr Svoboda','{"{"}"name":"Petr Svoboda"{"}"}');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Page}','{Space}','{Owner}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              VALUES('{Revision}','{Page}',1,'Zápis','{content}','Volal Petr Svoboda.','{Owner}',now(),'web');
            UPDATE "Page" SET "PublishedRevisionId" = '{Revision}' WHERE "Id" = '{Page}';
            -- More pages than one pass extracts: the sweep goes on where it stopped and then settles.
            CREATE TEMP TABLE more AS SELECT gen_random_uuid() AS id, gen_random_uuid() AS revision FROM generate_series(1, 250);
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") SELECT id, '{Space}', '{Owner}', 1 FROM more;
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              SELECT revision, id, 1, 'Další', '{content}', 'x', '{Owner}', now(), 'web' FROM more;
            UPDATE "Page" p SET "PublishedRevisionId" = m.revision FROM more m WHERE p."Id" = m.id;
            INSERT INTO "PagePublication"("Id","PageId","RevisionId","PublisherId","ActiveFrom","Channel") VALUES(gen_random_uuid(),'{Page}','{Revision}','{Owner}',now(),'web');
            DELETE FROM "OutboxMessage";
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, services => services.AddJob<ContentReferenceJob>(), TestModules.AllEnabled);
        long run;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            run = await scope.ServiceProvider.GetRequiredService<IJobRunner>().RunToEndAsync(ContentReferenceJob.JobId, "test");
        }
        // One run goes on past the full pass (200) and ends with the first that is not (51).
        Assert.Equal("succeeded:251", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT "State" || ':' || ("Summary"->>'sources') FROM "JobRun" WHERE "Id" = {run}
            """));
        Assert.Equal(0, await host.InvokeAsync<int>(new SweepContentReferences()));
        Assert.Equal($"rev:{Revision}:published:0:suggested:Petr Svoboda:object:{Petr}", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT string_agg("SourcePart" || ':' || "RevisionKind" || ':' || "Ordinal" || ':' || "State" || ':' || "Text" || ':' || array_to_string("Candidates", ','), '|')
            FROM "ContentReference" WHERE "SourceId" = '{Page}'
            """));
        Assert.Equal(251L, await OutboxTests.ScalarAsync(host.ConnectionString, """SELECT count(*) FROM "ContentReference" """));

        // A page written straight into the tables (an import, a restore) links like any other: link-only, so published.
        using (var session = host.OpenSession())
        {
            host.Actor.Current = await session.GetAsync<UserAccount>(Guid.Parse(Owner));
        }
        var linked = await host.InvokeAsync<ReferenceWriteResult>(new LinkPage(Guid.Parse(Page), 1));
        Assert.Equal((true, true, 2), (linked.Changed, linked.Published, linked.PublishedVersion));
        Assert.Equal(0, await host.InvokeAsync<int>(new SweepContentReferences()));
    }

    [PostgreSqlFact]
    public async Task Migration_2026110320_drops_the_dismissed_offers_and_leaves_the_pages_as_they_are()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_content_reference_drop_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110320);
        const string Owner = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a";
        const string Page = "00000000-0000-0000-0000-0000000000a1", Revision = "00000000-0000-0000-0000-0000000000b1";
        var content = Rich(Paragraph("Volal Petr Svoboda.")).Replace("'", "''");
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','writer','Writer',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Page}','{Space}','{Owner}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              VALUES('{Revision}','{Page}',1,'Zápis','{content}','Volal Petr Svoboda.','{Owner}',now(),'web');
            UPDATE "Page" SET "PublishedRevisionId" = '{Revision}' WHERE "Id" = '{Page}';
            INSERT INTO "ContentReferenceDismissal"("PageId","Kind","TextKey","DismissedById") VALUES('{Page}','person','petr svoboda','{Owner}');
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        Assert.Equal(0L, await OutboxTests.ScalarAsync(host.ConnectionString, """SELECT count(*) FROM information_schema.tables WHERE table_name = 'ContentReferenceDismissal'"""));
        Assert.Equal($"1:{content.Replace("''", "'")}", await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT p."Version" || ':' || r."ContentJson" FROM "Page" p JOIN "PageRevision" r ON r."Id" = p."PublishedRevisionId" WHERE p."Id" = '{Page}'
            """));
    }

    // A site of the class: no other class's directory names the same people.
    private static Task<McpSite> SiteAsync() => McpSite.CreateAsync(settings: settings => settings["Modules:Boards:Enabled"] = "true", sharedAs: nameof(ContentReferenceTests));

    private static Task<string> HomeAsync(McpSite f) => f.FirstSpaceAsync();

    /// <summary>A contact of the space's directory: every test has its own people, where the wiki's directory is everybody's.</summary>
    private static async Task<string> ContactAsync(McpSite f, string space, string name, string? organization) => (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post,
        "/api/objects", new { typeId = "atlas.contact", scope = "space", spaceId = space, data = new JsonObject { ["name"] = name, ["organization"] = organization } }), HttpStatusCode.Created))["id"]!.GetValue<string>();

    /// <summary>Sweeps until nothing is stale, as the job does.</summary>
    internal static async Task SweepAsync(McpSite f)
    {
        for (var i = 0; i < 50; i++)
        {
            await using var scope = f.Site.Services.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<int>(new SweepContentReferences()) == 0)
            {
                return;
            }
        }
        Assert.Fail("The sweep never settled.");
    }

    private static async Task<string?> RowsAsync(McpSite f, string sourceId, string part) => await f.Site.ScalarAsync("""
        SELECT string_agg("Ordinal" || ':' || "Kind" || ':' || "State" || ':' || "Text" || ':' || coalesce("Recognizer", ''), '|' ORDER BY "SourcePart", "Ordinal")
        FROM "ContentReference" WHERE "SourceId" = @id AND "SourcePart" LIKE @part
        """, ("id", Guid.Parse(sourceId)), ("part", part + "%")) as string;

    private static async Task<string> PublishedAsync(McpSite f, string pageId) => (string)(await f.Site.ScalarAsync("""
        SELECT r."ContentJson" FROM "Page" p JOIN "PageRevision" r ON r."Id" = p."PublishedRevisionId" WHERE p."Id" = @page
        """, ("page", Guid.Parse(pageId))))!;

    private static async Task<JsonNode> Expect(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private static string[] Texts(JsonNode list) => list["items"]!.AsArray().Select(x => x!["text"]!.GetValue<string>()).ToArray();

    private static JsonNode Ok(ModelContextProtocol.Protocol.CallToolResult result)
    {
        Assert.True(result.IsError != true, Text(result));
        return Structured(result);
    }
}
