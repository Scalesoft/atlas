using Atlas.Server.Features.Projector.Commands;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Queries;
using Xunit;
using static Atlas.Server.Tests.Projector.ProjectorDocuments;

namespace Atlas.Server.Tests.Projector;

/// <summary>The index kept by page saves and the reads over it and over catalog pages (spec §4), each filtered by page rights.</summary>
public sealed class ProjectorQueryTests
{
    [PostgreSqlFact]
    public async Task The_index_follows_a_save_and_a_publication_made_through_the_page_handlers()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("KBN", Json(Block("b1", "portal.ssl", "SSL", state: "done")));
        Assert.Equal("b1:done:tt", await f.IndexAsync(page));

        await f.SaveWorkingAsync(page, Json(Block("b1", "portal.ssl", "SSL", state: "blocked"), Block("b2", "portal.dns", "DNS")));
        Assert.Equal("b1:done:ft b1:blocked:tf b2:planned:tf", await f.IndexAsync(page));

        // A plain save through SavePage drops the working rows; publishing it drops the published ones.
        var current = await f.Pages.GetAsync(page, f.Writer, default);
        var saved = await f.Pages.SaveAsync(page, new SavePageRequest("KBN", Json(Paragraph("bez požadavků")), null, current.Version), f.Writer, default);
        Assert.Equal("b1:done:ft", await f.IndexAsync(page));
        await f.Pages.PublishAsync(page, new PublishPageRequest(saved.Version), f.Writer, default);
        Assert.Equal("", await f.IndexAsync(page));
    }

    [PostgreSqlFact]
    public async Task Page_requirements_give_an_editor_the_working_blocks_and_a_reader_the_published_ones()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("KBN", Json(Block("b1", "portal.ssl", "SSL", state: "done", direction: "inbound")));
        await f.SaveWorkingAsync(page, Json(Heading("Portál", "h-portal"), Heading("Core", "h-core", 3), Block("b1", "portal.ssl", "SSL", state: "waiting_for_input",
            direction: "inbound", party: "IT KBN", content: [Block("b2", "portal.orders", "Objednávky", requires: "portal.ssl, portal.elsewhere")]), Summary("s")));

        var editor = await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page));
        var reader = await f.As<PageRequirements>(f.Reader, new GetPageRequirements(page));

        Assert.True(editor.Working);
        Assert.Equal(["b1", "b2"], editor.Blocks.Select(x => x.BlockId));
        Assert.Equal("b1", editor.Blocks[1].ParentBlockId);
        Assert.Equal(["portal.ssl", "portal.elsewhere"], editor.Blocks[1].Requires);
        Assert.Equal(["portal.ssl"], editor.Blocks[1].WaitingOn);
        // Each block with its section, the nested one in its top-level block's (the rule of the overview and the board's lanes).
        Assert.All(editor.Blocks, block => Assert.Equal(["Portál", "Core"], block.Section));
        // The nested block is one block of the page, counted once.
        Assert.Equal((2, 0, 0, 1), (editor.Summary.Total, editor.Summary.Done, editor.Summary.Percent, editor.Summary.Waiting));
        Assert.Equal(["portal.ssl"], editor.Summary.InboundMissing.Select(x => x.Key));
        Assert.False(reader.Working);
        Assert.Equal(("done", 100, 0), (Assert.Single(reader.Blocks).State, reader.Summary.Percent, reader.Blocks[0].Section!.Count));
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<PageRequirements>(f.Outsider, new GetPageRequirements(page)))).Status);
    }

    /// <summary>Plan 3.6: a page's read takes the blocks from the revision it reads, not from the index, and who changed each block in
    /// one statement for the page; the opening it records included, as the web reads it. The reader's read of the publication was 14
    /// statements with the index (the editor's 13), now 13 (12).</summary>
    [PostgreSqlFact]
    public async Task A_page_read_stays_within_its_statement_budget()
    {
        const int Budget = 13;
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("KBN", Json(Block("b1", "portal.ssl", "SSL"), Block("b2", "portal.dns", "DNS", requires: "portal.ssl")));
        foreach (var actor in new[] { f.Writer, f.Reader })
        {
            await f.As<PageRequirements>(actor, new GetPageRequirements(page));
            using var recording = Platform.SqlRecording.Start();
            await f.As<PageRequirements>(actor, new GetPageRequirements(page));
            Assert.True(recording.Statements.Count <= Budget, $"{actor.Username}: {recording.Statements.Count} statements, budget {Budget}");
            Assert.DoesNotContain(recording.Statements, statement => statement.Contains("\"ProjectorRequirementIndex\"", StringComparison.Ordinal));
        }
    }

    [PostgreSqlFact]
    public async Task The_catalog_holds_the_published_catalog_pages_the_reader_may_read()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var shared = await f.PageAsync("Katalog", Json(Heading("Portál", "portal"),
            Block("c1", "portal.ssl", "SSL certifikát", direction: "inbound"), Block("c2", "portal.orders", "Objednávky", requires: "portal.ssl")));
        var restricted = await f.PageAsync("Tajný katalog", Json(Block("c3", "vpn", "VPN")));
        var draft = await f.PageAsync("Rozepsaný katalog", Json(Block("c4", "draft", "Koncept")), publish: false);
        var spare = await f.PageAsync("Záložní katalog", Json(Block("c5", "portal.ssl", "SSL jinak")));
        await f.PageAsync("Nasazení KBN", Json(Block("d1", "deployment", "Nasazení")));
        foreach (var catalog in new[] { shared, restricted, draft, spare })
        {
            await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(catalog, true));
        }
        await f.OwnAccessAsync(restricted);

        var read = await f.As<ProjectorCatalog>(f.Reader, new GetProjectorCatalog(f.SpaceId));
        var admin = await f.As<ProjectorCatalog>(f.Admin, new GetProjectorCatalog(f.SpaceId));

        Assert.Equal(["portal.ssl", "portal.orders"], read.Definitions.Select(x => x.Key));
        Assert.Equal(("Portál", shared, "inbound"), (read.Definitions[0].Bundle, read.Definitions[0].PageId, read.Definitions[0].Direction));
        Assert.Equal("c1", read.Definitions[0].ContentJson!["attrs"]!["id"]!.GetValue<string>());
        // The same key on two catalog pages: the page first by title wins, and the catalog says so.
        Assert.Equal(["Klíč „portal.ssl“ je v katalogu víckrát; platí první výskyt („SSL certifikát“)."], read.Warnings);
        // Catalog pages in the order of their titles; the unpublished one has no definitions yet.
        Assert.Equal(["portal.ssl", "portal.orders", "vpn"], admin.Definitions.Select(x => x.Key));
        var resolution = await f.As<RequirementResolution>(f.Reader, new ResolveProjectorRequirements(f.SpaceId, ["portal.orders"], []));
        Assert.Equal(["portal.ssl", "portal.orders"], resolution.Items.Select(x => x.Definition.Key));
        // P2-6: the panel lists the catalog without the block bodies and fetches the bodies of the keys it inserts.
        var light = await f.As<ProjectorCatalog>(f.Reader, new GetProjectorCatalog(f.SpaceId, Bodies: false));
        var picked = await f.As<ProjectorCatalog>(f.Reader, new GetProjectorCatalog(f.SpaceId, Keys: ["portal.orders"]));
        Assert.Equal(["portal.ssl", "portal.orders"], light.Definitions.Select(x => x.Key));
        Assert.All(light.Definitions, x => Assert.Null(x.ContentJson));
        Assert.Equal("c2", Assert.Single(picked.Definitions).ContentJson!["attrs"]!["id"]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task Marking_a_catalog_page_needs_an_editor_of_the_page()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("Katalog", Json(Block("c1", "portal.ssl", "SSL")));

        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<CatalogPageState>(f.Reader, new SetCatalogPage(page, true)))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<CatalogPageState>(f.Outsider, new SetCatalogPage(page, true)))).Status);
        Assert.True((await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(page, true))).IsCatalog);
        Assert.True((await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(page, true))).IsCatalog);
        Assert.Single((await f.As<ProjectorCatalog>(f.Reader, new GetProjectorCatalog(f.SpaceId))).Definitions);
        Assert.False((await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(page, false))).IsCatalog);
        Assert.Empty((await f.As<ProjectorCatalog>(f.Reader, new GetProjectorCatalog(f.SpaceId))).Definitions);
    }

    /// <summary>Review C2-3, C2-13, U2-5: the filter is decided in SQL alone, every page carries the percent of its summary whatever
    /// the filter keeps, and a catalog page is no deployment, so it is in neither the overview nor the waiting list.</summary>
    [PostgreSqlFact]
    public async Task The_overview_filters_in_sql_gives_each_page_its_percent_and_leaves_catalog_pages_out()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        await f.PageAsync("KBN", Json(Block("k1", "ssl", "SSL", state: "done", direction: "inbound"),
            Block("k2", "sms", "SMS brána", state: "waiting_for_input", direction: "inbound", party: "IT KBN"),
            Heading("Portál", "h-portal"), Block("k3", "orders", "Objednávky"), Block("k4", "nis", "NIS", state: "not_applicable")));
        var catalog = await f.PageAsync("Katalog", Json(Block("c1", "sms", "SMS brána", state: "waiting_for_input", direction: "inbound", party: "IT KBN")));
        await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(catalog, true));

        var missing = await f.As<RequirementOverview>(f.Reader, new GetProjectorOverview(f.SpaceId, "missing"));
        var inbound = await f.As<RequirementOverview>(f.Reader, new GetProjectorOverview(f.SpaceId, "inbound-missing"));
        var waiting = await f.As<WaitingList>(f.Reader, new GetProjectorWaiting(f.SpaceId));

        var page = Assert.Single(missing.Pages);
        Assert.Equal(("KBN", 33, 3), (page.Title, page.Percent, page.Applicable));
        Assert.Equal(["orders", "sms"], page.Cells.Select(x => x.Key));
        // Each cell with its section, so the matrix groups by section as the page does (one rule, RequirementText.Sectioned).
        Assert.Equal(["Portál", ""], page.Cells.Select(x => string.Join(" › ", x.Section!)));
        Assert.Equal(["sms"], Assert.Single(inbound.Pages).Cells.Select(x => x.Key));
        Assert.Equal(["KBN"], waiting.Parties.SelectMany(p => p.Items).Select(x => x.PageTitle));
        var refused = await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementOverview>(f.Reader, new GetProjectorOverview(f.SpaceId, "other")));
        Assert.Equal("invalid_filter", refused.Code);
        // Review S3-2: the waiting list's own filter is no overview filter.
        var waitingFilter = await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementOverview>(f.Reader, new GetProjectorOverview(f.SpaceId, "waiting")));
        Assert.Equal((400, "invalid_filter", "Filtr přehledu je missing, inbound-missing nebo all."), (waitingFilter.Status, waitingFilter.Code, waitingFilter.Message));
    }

    [PostgreSqlFact]
    public async Task The_overview_and_the_waiting_list_show_only_pages_the_reader_may_read_in_the_version_they_read()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var kbn = await f.PageAsync("KBN", Json(Block("k1", "sms", "SMS brána", state: "waiting_for_input", direction: "inbound", party: "IT KBN",
            date: "2026-10-01"), Block("k2", "ssl", "SSL", state: "done", direction: "inbound")));
        await f.SaveWorkingAsync(kbn, Json(Block("k1", "sms", "SMS brána", state: "done", direction: "inbound", party: "IT KBN"),
            Block("k2", "ssl", "SSL", state: "done", direction: "inbound")));
        var secret = await f.PageAsync("Tajné", Json(Block("t1", "sms", "SMS brána", state: "waiting_for_input", direction: "inbound", party: "Dodavatel")));
        await f.OwnAccessAsync(secret);
        await f.PageAsync("Koncept", Json(Block("p1", "ssl", "SSL", state: "waiting_for_input", direction: "inbound", party: "IT KBN")), publish: false);

        var reader = await f.As<RequirementOverview>(f.Reader, new GetProjectorOverview(f.SpaceId, "inbound-missing"));
        var writer = await f.As<RequirementOverview>(f.Writer, new GetProjectorOverview(f.SpaceId, "all"));
        var admin = await f.As<RequirementOverview>(f.Admin, new GetProjectorOverview(f.SpaceId, "inbound-missing"));

        Assert.Equal([("KBN", "sms")], reader.Pages.SelectMany(p => p.Cells.Select(c => (p.Title, c.Key))));
        Assert.Equal([("KBN", "sms:done"), ("KBN", "ssl:done"), ("Koncept", "ssl:waiting_for_input")],
            writer.Pages.SelectMany(p => p.Cells.Select(c => (p.Title, c.Key + ":" + c.State))));
        Assert.Equal(["Koncept", "Tajné"], admin.Pages.Select(p => p.Title));

        var readerWaiting = await f.As<WaitingList>(f.Reader, new GetProjectorWaiting(f.SpaceId));
        var adminWaiting = await f.As<WaitingList>(f.Admin, new GetProjectorWaiting(f.SpaceId));
        Assert.Equal([("IT KBN", "KBN")], readerWaiting.Parties.SelectMany(p => p.Items.Select(i => (p.Party, i.PageTitle))));
        Assert.Equal(["IT KBN", "Dodavatel"], adminWaiting.Parties.Select(p => p.Party));
        // A space the outsider may not list is missing, as its page tree is (not an empty list that tells the space exists).
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<WaitingList>(f.Outsider, new GetProjectorWaiting(f.SpaceId)))).Status);
    }

    /// <summary>Plan 3.1 §2.3: the definitions of one page to insert from, from the revision the actor reads, with bodies and modules.</summary>
    [PostgreSqlFact]
    public async Task A_page_gives_its_definitions_as_the_actor_reads_it()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("KBN", Json(Heading("Portál", "portal"), Block("d1", "portal.ssl", "SSL", direction: "inbound"),
            Block("d2", "portal.orders", "Objednávky", requires: "portal.ssl"), Block("d3", "portal.ssl", "SSL jinak")));
        await f.SaveWorkingAsync(page, Json(Block("d4", "vpn", "VPN")));

        var reader = await f.As<ProjectorCatalog>(f.Reader, new GetPageDefinitions(page));
        var writer = await f.As<ProjectorCatalog>(f.Writer, new GetPageDefinitions(page));

        Assert.Equal([("portal.ssl", "portal", "Portál"), ("portal.orders", "portal", "Portál")], reader.Definitions.Select(x => (x.Key, x.ModuleKey, x.Bundle)));
        Assert.Equal("d1", reader.Definitions[0].ContentJson!["attrs"]!["id"]!.GetValue<string>());
        Assert.Equal(["Klíč „portal.ssl“ je v katalogu víckrát; platí první výskyt („SSL“)."], reader.Warnings);
        Assert.Equal(["vpn"], writer.Definitions.Select(x => x.Key));
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<ProjectorCatalog>(f.Outsider, new GetPageDefinitions(page)))).Status);
    }

    /// <summary>Plan 3.1 §2.3: presets are the space templates with requirement blocks the actor may list, each with its count.</summary>
    [PostgreSqlFact]
    public async Task Presets_are_the_listed_templates_that_hold_requirement_blocks()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        // Requirement headings (3.6): a requirement cannot be nested, so the old parent and child are two siblings.
        var preset = await f.TemplateAsync("Předvolba portálu", Json(RequirementHeading("portal.ssl", "SSL"), RequirementHeading("portal.dns", "DNS")));
        await f.TemplateAsync("Zápis z porady", Json(Paragraph("Bez požadavků")));
        await f.PageAsync("KBN", Json(Block("k1", "portal.ssl", "SSL")));
        var closed = await f.TemplateAsync("Uzavřená předvolba", Json(RequirementHeading("vpn", "VPN")));
        await f.OwnAccessAsync(closed);

        var reader = await f.As<IReadOnlyList<ProjectorPreset>>(f.Reader, new GetProjectorPresets(f.SpaceId, Details: true));
        var admin = await f.As<IReadOnlyList<ProjectorPreset>>(f.Admin, new GetProjectorPresets(f.SpaceId));

        // The pickers' light answer reads no preset's content and no catalog: only the listing and the counts.
        var light = Assert.Single(await f.As<IReadOnlyList<ProjectorPreset>>(f.Reader, new GetProjectorPresets(f.SpaceId)));
        Assert.Equal((preset, "Předvolba portálu", 2, "", 0, null, null), (light.PageId, light.Title, light.Requirements, light.Summary, light.Modules, light.Products, light.Differs));
        var read = Assert.Single(reader);
        Assert.Equal((preset, "Předvolba portálu", 2, "", 1, 0), (read.PageId, read.Title, read.Requirements, read.Summary, read.Modules, read.Differs!.Count));
        Assert.Empty(read.Products!);
        Assert.Equal([preset, closed], admin.Select(x => x.PageId));
        Assert.Equal(["portal.ssl", "portal.dns"], (await f.As<ProjectorCatalog>(f.Reader, new GetPageDefinitions(preset))).Definitions.Select(x => x.Key));
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<IReadOnlyList<ProjectorPreset>>(f.Outsider, new GetProjectorPresets(f.SpaceId)))).Status);
    }

    [PostgreSqlFact]
    public async Task A_query_filters_every_block_of_the_space_by_state_party_date_and_module_and_pages_through_them()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        await f.PageAsync("Alfa", Json(Block("a1", "portal.ssl", "SSL", state: "waiting_for_input", party: "IT KBN", date: "2026-10-01"),
            Block("a2", "portal.orders", "Objednávky"), Block("a3", "", "Bez klíče")));
        await f.PageAsync("Beta", Json(Block("b1", "portal.ssl", "SSL", state: "done", party: "Dodavatel", date: "2026-11-15"),
            Block("b2", "net.dns", "DNS", state: "waiting_for_input", party: "it kbn", date: "2026-10-20")));
        var catalog = await f.PageAsync("Katalog", Json(Heading("Portál", "portal"), Block("c1", "portal.ssl", "SSL"), Block("c2", "portal.orders", "Objednávky"),
            Heading("Síť", "sit"), Block("c3", "net.dns", "DNS")));
        await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(catalog, true));
        async Task<string[]> Ids(QueryRequirements query) => (await f.As<RequirementQueryPage>(f.Reader, query)).Rows.Select(x => x.Requirement.BlockId).ToArray();

        Assert.Equal(["a1", "b2"], await Ids(new QueryRequirements(f.SpaceId, States: ["waiting_for_input"])));
        Assert.Equal(["a1", "b2"], await Ids(new QueryRequirements(f.SpaceId, Party: " it KBN ")));
        // The party is matched after the cheaper filters, over what they left.
        Assert.Equal(["b2"], await Ids(new QueryRequirements(f.SpaceId, Party: "it kbn", DateFrom: "2026-10-10")));
        Assert.Equal(["b1", "b2"], await Ids(new QueryRequirements(f.SpaceId, DateFrom: "2026-10-10", DateTo: "2026-11-15")));
        Assert.Equal(["a1", "a2", "b1"], await Ids(new QueryRequirements(f.SpaceId, Module: "portal")));
        Assert.Equal(["b2"], await Ids(new QueryRequirements(f.SpaceId, States: ["waiting_for_input"], Module: "sit")));
        // Plan 3.5 I: every word of text is part of the block's title, key or page title, whatever its case and diacritics.
        Assert.Equal(["b1"], await Ids(new QueryRequirements(f.SpaceId, Text: "ssl bet")));
        Assert.Equal(["a2"], await Ids(new QueryRequirements(f.SpaceId, Text: "OBJEDNÁVK")));
        Assert.Equal(["a1", "b1"], await Ids(new QueryRequirements(f.SpaceId, Text: "portal.ssl")));
        Assert.Equal(["a1"], await Ids(new QueryRequirements(f.SpaceId, States: ["waiting_for_input"], Text: "SSL")));
        var first = await f.As<RequirementQueryPage>(f.Reader, new QueryRequirements(f.SpaceId, Limit: 2));
        Assert.Equal(("a1,a2", 5, (int?)2), (string.Join(",", first.Rows.Select(x => x.Requirement.BlockId)), first.Total, first.NextOffset));
        Assert.Equal("Alfa", first.Rows[0].PageTitle);
        var last = await f.As<RequirementQueryPage>(f.Reader, new QueryRequirements(f.SpaceId, Offset: 4, Limit: 2));
        Assert.Equal(("b2", 5, (int?)null), (string.Join(",", last.Rows.Select(x => x.Requirement.BlockId)), last.Total, last.NextOffset));

        // Each refused filter names its field and what to send instead.
        foreach (var (query, field) in new (QueryRequirements, string)[]
                 {
                     (new(f.SpaceId, States: ["hotovo"]), "states"), (new(f.SpaceId, DateFrom: "2026-13-01"), "dateFrom"),
                     (new(f.SpaceId, DateFrom: "2026-12-01", DateTo: "2026-11-01"), "dateFrom"), (new(f.SpaceId, Limit: 0), "limit"),
                     (new(f.SpaceId, Limit: 201), "limit"), (new(f.SpaceId, Offset: -1), "offset"), (new(f.SpaceId, Module: "ucetnictvi"), "module")
                 })
        {
            var refused = await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementQueryPage>(f.Reader, query));
            Assert.Equal((400, "invalid_argument", field), (refused.Status, refused.Code, (string)refused.Extensions!["field"]!));
            Assert.StartsWith(field + ":", refused.Message);
        }
        Assert.Contains("projector_get_catalog", (await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementQueryPage>(f.Reader,
            new QueryRequirements(f.SpaceId, Module: "ucetnictvi")))).Message);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementQueryPage>(f.Outsider, new QueryRequirements(f.SpaceId)))).Status);
    }
}
