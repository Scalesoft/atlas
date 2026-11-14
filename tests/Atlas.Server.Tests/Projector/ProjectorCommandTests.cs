using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Projector.Commands;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Queries;
using Xunit;
using static Atlas.Server.Tests.Projector.ProjectorDocuments;

namespace Atlas.Server.Tests.Projector;

/// <summary>
/// The block commands MCP wraps (spec §4, §6) and the copy into a new deployment (§1.5): they write the working revision through the
/// shared page save, with its version check and rights. The saves validate the requirement nodes with the host manifest of R1.
/// </summary>
public sealed class ProjectorCommandTests
{
    // Block ids are UUIDs in the editor schema; the suffix keeps them readable in assertions.
    private const string B1 = "00000000-0000-4000-8000-0000000000b1", B2 = "00000000-0000-4000-8000-0000000000b2",
        B3 = "00000000-0000-4000-8000-0000000000b3", B4 = "00000000-0000-4000-8000-0000000000b4",
        C1 = "00000000-0000-4000-8000-0000000000c1", C2 = "00000000-0000-4000-8000-0000000000c2", C3 = "00000000-0000-4000-8000-0000000000c3";

    [PostgreSqlFact]
    public async Task Block_commands_change_the_working_revision_through_the_page_save_with_its_version_check()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var catalog = await f.PageAsync("Katalog", Json(Heading("Portál", "portal"), Block(C1, "portal.requirements", "Strukturované požadavky"),
            Block(C2, "portal.orders", "Objednávky", requires: "portal.requirements,portal.ssl")));
        await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(catalog, true));
        // Requirement headings (3.6): a requirement is addressed by its headingId and cannot hold another one.
        var page = await f.PageAsync("KBN", Json(Heading("Vstupy", "vstupy"), RequirementHeading("portal.ssl", "SSL", direction: "inbound"), Paragraph("konec")));
        var version = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Version;

        var updated = await f.As<RequirementWriteResult>(f.Writer, new UpdateRequirement(page, "portal.ssl", new RequirementChange(State: "done", Party: "IT KBN"), version));
        var inserted = await f.As<RequirementInsertResult>(f.Writer, new InsertRequirements(page, ["portal.orders", "unknown"],
            new RequirementPosition(AfterBlockId: "portal.ssl"), updated.Version));
        var moved = await f.As<RequirementWriteResult>(f.Writer, new MoveRequirement(page, inserted.Inserted[0].BlockId,
            new RequirementPosition(AfterBlockId: inserted.Inserted[1].BlockId), inserted.Version));
        var removed = await f.As<RequirementWriteResult>(f.Writer, new RemoveRequirement(page, inserted.Inserted[1].BlockId, moved.Version));

        Assert.Equal(version + 4, removed.Version);
        Assert.Equal([("portal.requirements", "Potřebuje ho: Objednávky"), ("portal.orders", "Vybráno")], inserted.Inserted.Select(x => (x.Key, x.Reason)));
        Assert.Equal(["Požadavek „unknown“ v katalogu není."], inserted.Warnings);
        var read = await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page));
        Assert.Equal([("portal.ssl", null), (inserted.Inserted[0].BlockId, null)], read.Blocks.Select(x => (x.BlockId, x.ParentBlockId)));
        Assert.Equal(("done", "IT KBN"), (read.Blocks[0].State, read.Blocks[0].Party));
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}$", read.Blocks[0].Completed);
        // The reader still sees the publication; a stale version, a reader and an outsider are refused before anything changes.
        Assert.Equal("planned", Assert.Single((await f.As<PageRequirements>(f.Reader, new GetPageRequirements(page))).Blocks).State);
        var stale = await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementWriteResult>(f.Writer, new RemoveRequirement(page, "portal.ssl", version)));
        Assert.Equal((409, "version_conflict"), (stale.Status, stale.Code));
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementWriteResult>(f.Reader, new RemoveRequirement(page, "portal.ssl", removed.Version)))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementWriteResult>(f.Outsider, new RemoveRequirement(page, "portal.ssl", removed.Version)))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementWriteResult>(f.Writer, new RemoveRequirement(page, "missing", removed.Version)))).Status);
    }

    [PostgreSqlFact]
    public async Task Insertion_and_removal_behave_as_a_user_expects_around_prerequisites()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var catalog = await f.PageAsync("Katalog", Json(Block(C1, "loop.a", "Smyčka A", requires: "loop.b"), Block(C2, "loop.b", "Smyčka B", requires: "loop.a"),
            Block(C3, "portal.orders", "Objednávky", requires: "portal.ssl")));
        await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(catalog, true));
        var page = await f.PageAsync("KBN", Json(Block(B1, "portal.ssl", "SSL", state: "not_applicable"), Block(B3, "portal.dns", "DNS"),
            Block(B4, "portal.check", "Kontrola", requires: "portal.dns")));
        var before = await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page));
        Assert.Equal(["portal.dns"], before.Blocks[2].WaitingOn);

        // A cycle names its path and inserts nothing.
        var cycle = await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementInsertResult>(f.Writer,
            new InsertRequirements(page, ["loop.a"], new RequirementPosition(), before.Version)));
        Assert.Equal("cyclic_dependency", cycle.Code);
        Assert.Contains("loop.a → loop.b → loop.a", cycle.Message);
        Assert.Equal(before.Version, (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Version);
        // A prerequisite the page marked not applicable stays as it is and is reported.
        var inserted = await f.As<RequirementInsertResult>(f.Writer, new InsertRequirements(page, ["portal.orders"], new RequirementPosition(), before.Version));
        Assert.Equal(["portal.orders"], inserted.Inserted.Select(x => x.Key));
        Assert.Equal(["Požadavek „portal.ssl“ už na stránce je, ale je označený jako nerelevantní; „Objednávky“ na něm závisí."], inserted.Warnings);
        // Removing what another block requires leaves that block waiting on nothing that is gone.
        var removed = await f.As<RequirementWriteResult>(f.Writer, new RemoveRequirement(page, B3, inserted.Version));
        var after = await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page));
        Assert.Equal(removed.Version, after.Version);
        Assert.Equal(["portal.ssl", "portal.check", "portal.orders"], after.Blocks.Select(x => x.Key));
        Assert.Empty(after.Blocks[1].WaitingOn);
        Assert.Equal(["portal.dns"], after.Blocks[1].Requires);
    }

    /// <summary>Plan 3.4 §4–§6: a whole module goes under its catalog heading below the product, its service comes along, and the page
    /// read gives each block's own state word and service from the page itself.</summary>
    [PostgreSqlFact]
    public async Task A_whole_module_is_inserted_under_its_heading_and_the_page_read_gives_the_own_state_word_and_the_service()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var cert = Block(C1, "nia.cert", "Certifikát", direction: "inbound");
        cert["attrs"]!["system"] = "NIA";
        var catalog = await f.PageAsync("Katalog", Json(Heading("Autentizace", "auth"), Heading("NIA", "nia", 3), cert,
            Block(C2, "nia.contract", "Smlouva", requires: "core.vpn"), Heading("Core", "core"), Block(C3, "core.vpn", "VPN")));
        await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(catalog, true));
        var page = await f.PageAsync("KBN", Json(Heading("Portál pacienta", "portal"), Block(B1, "intro", "Úvod")));
        var version = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Version;

        var unknown = await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementInsertResult>(f.Writer,
            new InsertRequirements(page, [], new RequirementPosition(HeadingId: "portal"), version, ["missing"])));
        Assert.Equal("invalid_argument", unknown.Code);
        var inserted = await f.As<RequirementInsertResult>(f.Writer, new InsertRequirements(page, [], new RequirementPosition(HeadingId: "portal"), version, ["nia"]));

        // What the module needs from another module first, without a heading of its own; the module under its heading one level below the product.
        Assert.Equal(["core.vpn", "nia.cert", "nia.contract"], inserted.Inserted.Select(x => x.Key));
        var read = await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page));
        Assert.Equal([("intro", "Portál pacienta"), ("core.vpn", "Portál pacienta"), ("nia.cert", "Portál pacienta › NIA"), ("nia.contract", "Portál pacienta › NIA")],
            read.Blocks.Select(x => (x.Key, string.Join(" › ", x.Section!))));
        var niaCert = read.Blocks.Single(x => x.Key == "nia.cert");
        Assert.Equal(("NIA", ""), (niaCert.System, niaCert.StateLabel));
        var labelled = await f.As<RequirementWriteResult>(f.Writer, new UpdateRequirement(page, niaCert.BlockId,
            new RequirementChange(State: "in_progress", StateLabel: "ověřit"), read.Version));
        var after = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Blocks.Single(x => x.Key == "nia.cert");
        Assert.Equal(("in_progress", "ověřit"), (after.State, after.StateLabel));
        // The module again: everything is on the page, nothing comes, no second heading.
        var again = await f.As<RequirementInsertResult>(f.Writer, new InsertRequirements(page, [], new RequirementPosition(HeadingId: "portal"), labelled.Version, ["nia"]));
        Assert.Empty(again.Inserted);
    }

    [PostgreSqlFact]
    public async Task Several_changes_go_in_one_save_and_a_renamed_key_takes_its_dependents_along()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("KBN", Json(Block(B1, "portal.ssl", "SSL"), Block(B2, "portal.orders", "Objednávky", requires: "portal.ssl")));
        var version = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Version;

        var saved = await f.As<RequirementWriteResult>(f.Writer, new UpdateRequirements(page,
            [new RequirementBlockChange(B1, new RequirementChange(Key: "portal.tls", State: "done")), new RequirementBlockChange(B2, new RequirementChange(Party: "IT KBN"))], version));

        Assert.Equal((page, version + 1), (saved.PageId, saved.Version));
        Assert.Equal(["Závislosti požadavků „Objednávky“ teď vedou na nový klíč „portal.tls“."], saved.Warnings);
        var blocks = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Blocks;
        Assert.Equal([("portal.tls", "done", ""), ("portal.orders", "planned", "IT KBN")], blocks.Select(x => (x.Key, x.State, x.Party)));
        Assert.Equal(["portal.tls"], blocks[1].Requires);
        // One refused change refuses the whole batch.
        var refused = await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementWriteResult>(f.Writer, new UpdateRequirements(page,
            [new RequirementBlockChange(B1, new RequirementChange(State: "planned")), new RequirementBlockChange(B2, new RequirementChange(State: "foo"))], saved.Version)));
        Assert.Equal("invalid_requirement", refused.Code);
        // The refusal names the change: its number in the message, its index (0-based) and blockId in the details, beside the field.
        Assert.StartsWith($"Změna č. 2 (blockId {B2}): Neznámý stav požadavku", refused.Message);
        Assert.Equal((1, B2, "state"), ((int)refused.Extensions!["index"]!, (string)refused.Extensions["blockId"]!, (string)refused.Extensions["field"]!));
        Assert.Equal("done", (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Blocks[0].State);
    }

    [PostgreSqlFact]
    public async Task A_new_block_is_inserted_empty_or_filled_at_its_position()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("KBN", Json(Heading("Vstupy", "vstupy"), RequirementHeading("portal.ssl", "SSL"), Heading("Konec", "konec"), Paragraph("konec")));
        var version = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Version;

        var empty = await f.As<RequirementInsertResult>(f.Writer, new CreateRequirement(page, new RequirementChange(), new RequirementPosition(), version));
        var filled = await f.As<RequirementInsertResult>(f.Writer, new CreateRequirement(page,
            new RequirementChange(Title: "Přístup do VPN", State: "waiting_for_input", Party: "IT KBN", Requires: ["portal.ssl"], NotesMarkdown: "Čeká na smlouvu."),
            new RequirementPosition(HeadingId: "vstupy"), empty.Version));

        Assert.Equal(version + 2, filled.Version);
        // A requirement heading always has an id: an empty one is given a key of its own.
        Assert.Equal(("Nový požadavek", true), (empty.Inserted[0].Reason, empty.Inserted[0].Key.StartsWith("pozadavek-", StringComparison.Ordinal)));
        Assert.Equal("pristup-do-vpn", filled.Inserted[0].Key);
        var blocks = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Blocks;
        Assert.Equal(["portal.ssl", filled.Inserted[0].BlockId, empty.Inserted[0].BlockId], blocks.Select(x => x.BlockId));
        Assert.Equal(("pristup-do-vpn", "Přístup do VPN", "waiting_for_input", "IT KBN", "internal", "portal.ssl"),
            (blocks[1].Key, blocks[1].Title, blocks[1].State, blocks[1].Party, blocks[1].Direction, string.Join(",", blocks[1].Requires)));
        Assert.Equal((empty.Inserted[0].Key, "", "planned"), (blocks[2].Key, blocks[2].Title, blocks[2].State));
        Assert.Contains("Čeká na smlouvu.", (await f.Pages.GetAsync(page, f.Writer, default)).ContentJson);
        // A refused field names itself and changes nothing; a reader may not add a block.
        var refused = await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementInsertResult>(f.Writer,
            new CreateRequirement(page, new RequirementChange(State: "hotovo"), new RequirementPosition(), filled.Version)));
        Assert.Equal((400, "invalid_requirement", "state"), (refused.Status, refused.Code, (string)refused.Extensions!["field"]!));
        Assert.Contains("planned, waiting_for_input", refused.Message);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementInsertResult>(f.Reader,
            new CreateRequirement(page, new RequirementChange(), new RequirementPosition(), filled.Version)))).Status);
        Assert.Equal(filled.Version, (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Version);
    }

    [PostgreSqlFact]
    public async Task Blocks_of_another_deployment_are_inserted_with_their_dependencies_and_reset()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var source = await f.PageAsync("KBN — Production", Json(Heading("Portál", "portal"),
            Block(C1, "portal.orders", "Objednávky", requires: "portal.ssl", state: "in_progress", party: "IT KBN"),
            Block(C2, "portal.ssl", "SSL", state: "done", direction: "inbound", date: "2026-09-10", party: "IT KBN", where: "Keepass"),
            Block(C3, "portal.dns", "DNS", content: [Paragraph("Poznámka")])));
        var page = await f.PageAsync("Kostelec", Json(Block(B1, "portal.dns", "DNS")));
        var version = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Version;

        // A subset pulls what it requires; the copies are reset and keep key, direction, requires and notes.
        var inserted = await f.As<RequirementInsertResult>(f.Writer, new InsertFromDeployment(page, source, ["portal.orders"], new RequirementPosition(), version));
        Assert.Equal([("portal.ssl", "Potřebuje ho: Objednávky"), ("portal.orders", "Vybráno")], inserted.Inserted.Select(x => (x.Key, x.Reason)));
        var blocks = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Blocks;
        Assert.Equal([("portal.dns", "planned", "", "", "", "internal"), ("portal.ssl", "planned", "", "", "", "inbound"), ("portal.orders", "planned", "", "", "", "internal")],
            blocks.Select(x => (x.Key, x.State, x.Date, x.Party, x.Where, x.Direction)));
        Assert.DoesNotContain(blocks, x => x.BlockId is C1 or C2 or C3);
        Assert.Equal(["portal.ssl"], blocks[2].Requires);
        // Without keys: every definition of the source, and what the page already holds is skipped and named.
        var again = await f.As<RequirementInsertResult>(f.Writer, new InsertFromDeployment(page, source, null, new RequirementPosition(), inserted.Version));
        Assert.Empty(again.Inserted);
        Assert.Equal(new[] { "portal.orders", "portal.ssl", "portal.dns" }.Select(key => $"Požadavek „{key}“ už na stránce je; znovu se nevloží."), again.Warnings);

        // The page itself is no source; a source the actor may not read is a missing page; a reader of the target may not write.
        var self = await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementInsertResult>(f.Writer,
            new InsertFromDeployment(page, page, null, new RequirementPosition(), again.Version)));
        Assert.Equal((400, "invalid_argument", "sourcePageId"), (self.Status, self.Code, (string)self.Extensions!["field"]!));
        await f.OwnAccessAsync(source);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementInsertResult>(f.Writer,
            new InsertFromDeployment(page, source, null, new RequirementPosition(), again.Version)))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementInsertResult>(f.Reader,
            new InsertFromDeployment(page, source, null, new RequirementPosition(), again.Version)))).Status);
        Assert.Equal(again.Version, (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Version);
    }

    [PostgreSqlFact]
    public async Task A_page_from_a_preset_starts_its_requirements_afresh_and_other_templates_copy_as_they_are()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        // Requirement headings (3.6): the old nested pair is two siblings, the dependency a link in the preset's own page.
        var preset = await f.TemplateAsync("Předvolba portálu", Json(Paragraph("Úvod")));
        await f.WriteContentAsync(preset, Json(Paragraph("Úvod"),
            Aliased(RequirementHeading("portal.ssl", "SSL", "done", "inbound", party: "IT KBN")), Dependencies((preset, "portal.dns")), Paragraph("do 10. 9. 2026"),
            Paragraph("Kde uloženo: Trezor KBN"), Paragraph("Poznámka"), RequirementHeading("portal.dns", "DNS", "blocked")));
        var plain = Json(Heading("Kontakty", "kontakty"), Paragraph("Bez požadavků"));
        var template = await f.TemplateAsync("Zápis z porady", plain);

        var page = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Kostelec", Json(Paragraph("x")), null, TemplatePageId: preset), f.Writer, default);
        var copy = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Porada", Json(Paragraph("x")), null, TemplatePageId: template), f.Writer, default);

        var blocks = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page.Id))).Blocks;
        Assert.Equal([("portal.ssl", "planned", "", "", "", "inbound", "portal.dns"), ("portal.dns", "planned", "", "", "", "internal", "")],
            blocks.Select(x => (x.Key, x.State, x.Date, x.Party, x.Where, x.Direction, string.Join(",", x.Requires))));
        Assert.Contains("Poznámka", page.ContentJson);
        AssertReset(page.ContentJson);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(plain)!["document"], JsonNode.Parse(copy.ContentJson)!["document"]));
        // The preset itself keeps its states.
        Assert.Equal("done", (await f.As<PageRequirements>(f.Admin, new GetPageRequirements(preset))).Blocks[0].State);
    }

    [PostgreSqlFact]
    public async Task Images_in_a_copied_page_or_an_inserted_catalog_block_are_copied_to_the_target_page()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var source = await f.PageAsync("KBN", Json(Paragraph("x")), publish: false);
        var sourceImage = await f.ImageAsync(source);
        await f.WriteContentAsync(source, Json(Block(B1, "portal.logo", "Logo", content: [Image(sourceImage)])));
        var catalog = await f.PageAsync("Katalog", Json(Paragraph("x")), publish: false);
        var catalogImage = await f.ImageAsync(catalog);
        await f.WriteContentAsync(catalog, Json(Block(C1, "portal.schema", "Schéma", content: [Image(catalogImage)])));
        await f.Pages.PublishAsync(catalog, new PublishPageRequest((await f.Pages.GetAsync(catalog, f.Writer, default)).Version), f.Writer, default);
        await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(catalog, true));

        var copy = await f.As<PageDto>(f.Writer, new CreateDeploymentFromPage(source, f.SpaceId, null, "Kostelec"));
        var inserted = await f.As<RequirementInsertResult>(f.Writer, new InsertRequirements(copy.Id, ["portal.schema"], new RequirementPosition(), copy.Version));

        var content = (await f.Pages.GetAsync(copy.Id, f.Writer, default)).ContentJson;
        Assert.Equal(2, await f.ScalarAsync("""SELECT count(*) FROM "Attachment" WHERE "PageId"=:id AND "ActiveTo" IS NULL""", copy.Id));
        Assert.DoesNotContain(sourceImage.ToString("D"), content);
        Assert.DoesNotContain(catalogImage.ToString("D"), content);
        Assert.Equal(2, await f.ScalarAsync("""
            SELECT count(*) FROM "PageRevisionAttachment" a JOIN "PageRevision" r ON r."Id"=a."RevisionId" WHERE r."PageId"=:id AND r."ActiveTo" IS NULL
            """, copy.Id));
        Assert.Equal(copy.Version + 1, inserted.Version);
    }

    /// <summary>Review S2-4: an image copied for a write that then fails (the save refuses the page) leaves no file behind; a copy
    /// whose grants a temporary administrator could not carry whole no longer fails (A3-3).</summary>
    [PostgreSqlFact]
    public async Task A_write_that_fails_after_copying_images_leaves_no_files()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var catalog = await f.PageAsync("Katalog", Json(Paragraph("x")), publish: false);
        var catalogImage = await f.ImageAsync(catalog);
        await f.WriteContentAsync(catalog, Json(Block(C1, "portal.schema", "Schéma", content: [Image(catalogImage)])));
        await f.Pages.PublishAsync(catalog, new PublishPageRequest((await f.Pages.GetAsync(catalog, f.Writer, default)).Version), f.Writer, default);
        await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(catalog, true));
        // A page the save refuses whole: a block of it has a state outside the node contract.
        var broken = await f.PageAsync("Rozbitá", Json(Block(B1, "portal.ssl", "SSL", state: "foo")), publish: false);
        var files = f.Host.Services.GetRequiredService<AttachmentFiles>();
        // The store is shared with the host's other tests: count the files holding an image of this test (the original and its copies).
        int Files(Guid image)
        {
            var bytes = File.ReadAllBytes(files.PathFor(image));
            return Directory.EnumerateFiles(files.Root, "*", SearchOption.AllDirectories).Count(path =>
            {
                try
                {
                    return File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
                }
                catch (IOException)
                {
                    return false; // another test's file being written or removed right now
                }
            });
        }
        var before = Files(catalogImage);

        var version = (await f.Pages.GetAsync(broken, f.Writer, default)).Version;
        var refused = await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementInsertResult>(f.Writer,
            new InsertRequirements(broken, ["portal.schema"], new RequirementPosition(), version)));
        Assert.Equal(400, refused.Status);
        Assert.Equal(before, Files(catalogImage));

        // Review A3-3: a temporary space administrator's copy of a page with longer grants no longer fails (its grants end with the
        // administrator's authority, ProjectorRightsTests): the copied image stays with the copy.
        var confidential = await f.PageAsync("Důvěrné", Json(Paragraph("x")));
        var image = await f.ImageAsync(confidential);
        await f.WriteContentAsync(confidential, Json(Block(B2, "portal.logo", "Logo", content: [Image(image)])));
        await f.OwnAccessAsync(confidential);
        await f.GrantAsync(confidential, f.Reader, 1);
        var temporary = await f.Host.SeedUserAsync("temporary");
        await f.Host.ExecuteAsync($"""
            INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","Role","ExpiresAt") VALUES (gen_random_uuid(),:id,'{temporary.Id}',3,now()+interval '1 day')
            """, f.SpaceId);
        var middle = Files(image);

        var copy = await f.As<PageDto>(temporary, new CreateDeploymentFromPage(confidential, f.SpaceId, null, "Kopie"));
        Assert.Single(copy.Warnings!);
        Assert.Equal(middle + 1, Files(image));
    }

    [PostgreSqlFact]
    public async Task Content_nested_deeper_than_the_default_json_depth_is_read_copied_and_inserted()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        JsonNode deep = Paragraph("dno");
        for (var i = 0; i < 40; i++)
        {
            deep = new JsonObject { ["type"] = "blockquote", ["content"] = new JsonArray(deep) };
        }
        var catalog = await f.PageAsync("Katalog", Json(Block(C1, "portal.deep", "Hluboký", content: [deep])));
        await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(catalog, true));
        var page = await f.PageAsync("KBN", Json(Block(B1, "portal.ssl", "SSL")));

        Assert.Equal("portal.deep", Assert.Single((await f.As<ProjectorCatalog>(f.Reader, new GetProjectorCatalog(f.SpaceId))).Definitions).Key);
        var inserted = await f.As<RequirementInsertResult>(f.Writer, new InsertRequirements(page, ["portal.deep"], new RequirementPosition(),
            (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Version));
        var copy = await f.As<PageDto>(f.Writer, new CreateDeploymentFromPage(page, f.SpaceId, null, "Kopie"));

        Assert.Equal(["portal.deep"], inserted.Inserted.Select(x => x.Key));
        Assert.Equal(2, (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(copy.Id))).Blocks.Count);
    }

    [PostgreSqlFact]
    public async Task Two_copies_in_opposite_directions_between_two_spaces_never_deadlock()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var other = await f.Host.SeedSpaceAsync("Jiný prostor", (f.Writer, SpaceRole.Writer));
        var here = await f.PageAsync("Tady", Json(Block(B1, "portal.ssl", "SSL")));
        var there = (await f.Pages.CreateAsync(new CreatePageRequest(other, null, "Tam", Json(Paragraph("tam")), null), f.Writer, default)).Id;

        for (var run = 0; run < 20; run++)
        {
            await Task.WhenAll(
                Task.Run(() => f.As<PageDto>(f.Writer, new CreateDeploymentFromPage(here, other, null, $"Tam {run}"))),
                Task.Run(() => f.As<PageDto>(f.Writer, new CreateDeploymentFromPage(there, f.SpaceId, null, $"Tady {run}"))));
        }

        Assert.Equal(21, await f.ScalarAsync("""SELECT count(*) FROM "Page" WHERE "SpaceId"=:id""", other));
    }

    [PostgreSqlFact]
    public async Task A_page_without_requirement_blocks_copies_as_it_is()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var source = await f.PageAsync("Šablona zakázky", Json(Heading("Úvod", "uvod"), Paragraph("Bez požadavků")));

        var copy = await f.As<PageDto>(f.Writer, new CreateDeploymentFromPage(source, f.SpaceId, source, "Nová zakázka"));

        Assert.Equal(("Nová zakázka", source), (copy.Title, copy.ParentId));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Json(Heading("Úvod", "uvod"), Paragraph("Bez požadavků")))!["document"],
            JsonNode.Parse(copy.ContentJson)!["document"]));
        Assert.Empty((await f.As<PageRequirements>(f.Writer, new GetPageRequirements(copy.Id))).Blocks);
    }

    /// <summary>O8 (owner 2. 11. 2026): the copy keeps the structure, not the job's values. A table outside the blocks keeps its header and
    /// first column, the section Kontakty only its heading (its sub-sections too go); the block follows plan 3.5 C.</summary>
    [PostgreSqlFact]
    public async Task A_deployment_copy_empties_table_values_and_the_contacts_section()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var source = await f.PageAsync("KBN — Production", Json(Heading("Servery", "servery"),
            Table(["Server", "IP", "OS"], ["app01", "10.0.0.1", "Debian"], ["db01", "10.0.0.2", "Ubuntu"]),
            Heading("Kontakty", "kontakty"), Paragraph("Jan Novák, 777 123 456"), Heading("Zákazník", "zakaznik", 3), Paragraph("IT KBN"),
            Heading("Portál", "portal"),
            Block(B1, "portal.ssl", "SSL", state: "done", party: "IT KBN", content: [Paragraph("Certifikát od CA"), Table(["Klíč", "Hodnota"], ["CN", "kbn.example.test"])])));

        var copy = await f.As<PageDto>(f.Writer, new CreateDeploymentFromPage(source, f.SpaceId, null, "Kostelec — Production"));

        static IEnumerable<string> Texts(JsonNode? node) => node switch
        {
            JsonObject { } text when text["type"]?.GetValue<string>() == "text" => [text["text"]!.GetValue<string>()],
            JsonObject element => Texts(element["content"]),
            JsonArray items => items.SelectMany(Texts),
            _ => [],
        };
        Assert.Equal(["Servery", "Server", "IP", "OS", "app01", "db01", "Kontakty", "Portál", "SSL", "Certifikát od CA", "Klíč", "Hodnota", "CN", "kbn.example.test"],
            Texts(JsonNode.Parse(copy.ContentJson)!["document"]));
        var block = Assert.Single((await f.As<PageRequirements>(f.Writer, new GetPageRequirements(copy.Id))).Blocks);
        Assert.Equal(("portal.ssl", "planned", ""), (block.Key, block.State, block.Party));
    }

    [PostgreSqlFact]
    public async Task A_deployment_copies_the_page_with_its_requirement_headings_reset_state_date_party_where_and_aliases()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var source = await f.PageAsync("KBN — Production", Json(Paragraph("Zakázka KBN")));
        // Requirement headings (3.6): the old nested pair is two siblings, so the copy has no parent block to compare.
        await f.WriteContentAsync(source, Json(Paragraph("Zakázka KBN"),
            Aliased(RequirementHeading("portal.ssl", "SSL", "done", "inbound", party: "IT KBN")), Dependencies((source, "portal.dns")), Paragraph("do 10. 9. 2026"),
            Paragraph("Kde uloženo: Trezor KBN"), Paragraph("Poznámka"), RequirementHeading("portal.dns", "DNS", "blocked")));

        var copy = await f.As<PageDto>(f.Writer, new CreateDeploymentFromPage(source, f.SpaceId, null, "Kostelec — Production"));

        Assert.Equal("Kostelec — Production", copy.Title);
        var blocks = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(copy.Id))).Blocks;
        Assert.Equal([("portal.ssl", "planned", "", "", "", "inbound", "portal.dns"), ("portal.dns", "planned", "", "", "", "internal", "")],
            blocks.Select(x => (x.Key, x.State, x.Date, x.Party, x.Where, x.Direction, string.Join(",", x.Requires))));
        Assert.Contains("Poznámka", copy.ContentJson);
        AssertReset(copy.ContentJson);
        await Assert.ThrowsAsync<ApiException>(() => f.As<PageDto>(f.Reader, new CreateDeploymentFromPage(source, f.SpaceId, null, "Kopie")));
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<PageDto>(f.Outsider, new CreateDeploymentFromPage(source, f.SpaceId, null, "Kopie")))).Status);
    }

    /// <summary>A heading with the former ids a 3.6 conversion left in its aliases.</summary>
    private static JsonObject Aliased(JsonObject heading)
    {
        heading["attrs"]!["aliases"] = "00000000-0000-4000-8000-0000000000e1,portal.ssl-old";
        return heading;
    }

    /// <summary>A copy for a new deployment holds neither the source's „Kde uloženo“ line nor its headings' aliases.</summary>
    private static void AssertReset(string contentJson)
    {
        Assert.DoesNotContain("Kde uloženo", contentJson, StringComparison.Ordinal);
        Assert.All(JsonNode.Parse(contentJson)!["document"]!["content"]!.AsArray().Where(x => (string?)x!["type"] == "heading"),
            heading => Assert.True(string.IsNullOrEmpty((string?)heading!["attrs"]!["aliases"]), heading!.ToJsonString()));
    }
}
