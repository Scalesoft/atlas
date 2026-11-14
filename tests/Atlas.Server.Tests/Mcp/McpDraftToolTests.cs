using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.Mcp.Domain;
using Atlas.Server.Platform.Content;
using Atlas.Server.Tests.Content;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Mcp;

/// <summary>
/// The draft tools (MCP plan C2) through the C# SDK client: drafts only, authored by the token owner with the channel mcp and the
/// token, the live-edit guard of D3, CAS between the read and the write, scopes and content rights, components and G5, and an audit
/// row for every attempt without content.
/// </summary>
public sealed class McpDraftToolTests
{
    private static readonly string[] DraftTools =
        ["atlas_create_draft", "atlas_insert_section", "atlas_rename_page", "atlas_replace_page", "atlas_update_section"];

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Draft_writes_are_authored_by_the_owner_with_mcp_channel()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Postup VPN", Rich(Heading(1, "uvod", "Úvod"), Paragraph("Publikovaný úvod."),
            Heading(2, "postup", "Postup"), Paragraph("Starý postup.")), "Publikovaný úvod.");
        var (tokenId, client) = await DraftClientAsync(f);
        var working = await CallAsync(client, "atlas_get_page", new { pageId = page.Id, version = "working" });
        var expected = Structured(working)["expectedVersion"]!.GetValue<int>();

        var updated = await CallAsync(client, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = expected, headingId = "postup", markdown = "## Postup upravený\n\nNový postup ZAPISKANAREK.\n"
        });
        Assert.True(updated.IsError != true, Text(updated));
        var result = Structured(updated);
        Assert.Equal(expected + 1, result["version"]!.GetValue<int>());
        Assert.Equal(expected + 1, result["expectedVersion"]!.GetValue<int>());
        Assert.EndsWith("/#/pages/" + page.Id, result["url"]!.GetValue<string>());
        Assert.Empty(result["warnings"]!.AsArray());
        Assert.Contains("pracovní verze", Text(updated));

        // The revision: authored by the token owner, channel mcp and the token; the heading kept its id (?heading= still works).
        Assert.Equal(Guid.Parse(f.AdminId), await CurrentAsync<Guid>(f, page.Id, "AuthorId"));
        Assert.Equal("mcp", await CurrentAsync<string>(f, page.Id, "Channel"));
        Assert.Equal(Guid.Parse(tokenId), await CurrentAsync<Guid>(f, page.Id, "ApiTokenId"));
        var section = await CallAsync(client, "atlas_get_page", new { pageId = page.Id, version = "working", headingId = "postup" });
        Assert.Contains("## Postup upravený", Text(section));
        Assert.Contains("Nový postup ZAPISKANAREK.", Text(section));
        // Readers still see the publication.
        var published = await CallAsync(client, "atlas_get_page", new { pageId = page.Id });
        Assert.Contains("Starý postup.", Text(published));
        Assert.DoesNotContain("ZAPISKANAREK", Text(published));
        Assert.Equal(1L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PagePublication\" WHERE \"PageId\"=@page", ("page", Guid.Parse(page.Id))));

        // The audit: one mcp.write per attempt, with the tool, the page, the outcome and the version; never content.
        var refused = await CallAsync(client, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = expected, headingId = "postup", markdown = "## Postup\n\nZASTARALYKANAREK.\n"
        });
        Assert.Equal("version_conflict", ErrorCode(refused));
        Assert.Equal(expected + 1, Structured(refused)["currentVersion"]!.GetValue<int>());
        Assert.Equal(["atlas_update_section:ok:" + (expected + 1), "atlas_update_section:version_conflict:"], await AuditAsync(f, tokenId));
        Assert.Equal(2L, await f.Site.ScalarAsync("""
            SELECT count(*) FROM "AccessAuditEvent" e JOIN "ApiActivity" a ON a."Id"=e."ApiActivityId"
            WHERE a."TokenId"=@token AND e."Action"='mcp.write' AND a."PageId"=@page AND a."Channel"='mcp'
            """, ("token", Guid.Parse(tokenId)), ("page", Guid.Parse(page.Id))));
        var rows = (string)(await f.Site.ScalarAsync("""
            SELECT string_agg(row_to_json(a)::text || row_to_json(e)::text, '') FROM "ApiActivity" a JOIN "AccessAuditEvent" e ON e."ApiActivityId"=a."Id"
            WHERE a."TokenId"=@token
            """, ("token", Guid.Parse(tokenId))))!;
        var log = string.Join("\n", f.Site.Logs.Messages);
        foreach (var canary in new[] { "ZAPISKANAREK", "ZASTARALYKANAREK", "Postup upravený", "Postup VPN" })
        {
            Assert.DoesNotContain(canary, rows, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(canary, log, StringComparison.OrdinalIgnoreCase);
        }
    }

    [PostgreSqlFact]
    public async Task Create_insert_replace_and_rename_write_drafts()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        // Titles of this test's own: links and similar pages are looked up on the administrator's rights, every test's pages of the site.
        var manualTitle = "Síťový manuál " + Word();
        var manual = await PageAsync(f.Admin, space, manualTitle, Rich(Paragraph("Manuál.")), "Manuál.");
        var parent = await PageAsync(f.Admin, space, "Provoz", Rich(Paragraph("Provoz.")), "Provoz.");
        var template = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/pages", new
        {
            spaceId = space, parentId = (string?)null, title = "Šablona postupu", isTemplate = true,
            contentJson = Rich(Heading(1, "sablona", "Postup"), Paragraph("Krok ze šablony.")), markdown = "# Postup\n\nKrok ze šablony."
        }), HttpStatusCode.OK);
        var (_, client) = await DraftClientAsync(f);

        // Links resolve on the owner's rights to the editor's page reference; an unknown title is only warned about.
        var linked = await CallAsync(client, "atlas_create_draft", new
        {
            spaceId = space, title = Word(), markdown = $"Viz [[{manualTitle.Replace("Síťový manuál", "síťový Manuál", StringComparison.Ordinal)}]] a [[Neexistující stránka {Word()}]]."
        });
        Assert.True(linked.IsError != true, Text(linked));
        Assert.Contains("Neexistující stránka", Assert.Single(Structured(linked)["warnings"]!.AsArray())!.GetValue<string>());
        var json = await CurrentAsync<string>(f, Structured(linked)["pageId"]!.GetValue<string>(), "ContentJson");
        Assert.Contains("\"pageReference\"", json);
        Assert.Contains(manual.Id, json);
        Assert.Empty(Structured(linked)["similarPages"]!.AsArray());

        // P15 parity: a title like an existing page's reports that page (never the new draft itself); the draft is still created.
        var duplicate = await CallAsync(client, "atlas_create_draft", new { spaceId = space, title = manualTitle, markdown = "Znovu." });
        Assert.True(duplicate.IsError != true, Text(duplicate));
        var similar = Assert.Single(Structured(duplicate)["similarPages"]!.AsArray())!;
        Assert.Equal((manual.Id, manualTitle), (similar["pageId"]!.GetValue<string>(), similar["title"]!.GetValue<string>()));
        Assert.Contains("Podobná stránka už existuje: " + manualTitle, Text(duplicate));

        // A new draft under a parent: unpublished, channel mcp.
        var created = await CallAsync(client, "atlas_create_draft", new
        {
            spaceId = space, parentId = parent.Id, title = "Nový postup", markdown = "# Nový postup\n\nÚvod.\n\n## Kroky\n\n1. Připojit\n2. Ověřit\n"
        });
        Assert.True(created.IsError != true, Text(created));
        var draftId = Structured(created)["pageId"]!.GetValue<string>();
        Assert.Equal(1, Structured(created)["version"]!.GetValue<int>());
        Assert.Empty(Structured(created)["warnings"]!.AsArray());
        Assert.Equal(Guid.Parse(parent.Id), await f.Site.ScalarAsync("SELECT \"ParentId\" FROM \"Page\" WHERE \"Id\"=@page", ("page", Guid.Parse(draftId))));
        Assert.Equal(DBNull.Value, await f.Site.ScalarAsync("SELECT \"PublishedRevisionId\" FROM \"Page\" WHERE \"Id\"=@page", ("page", Guid.Parse(draftId))) ?? DBNull.Value);
        Assert.Equal("mcp", await CurrentAsync<string>(f, draftId, "Channel"));

        // A section after "Kroky", one at the start, then the page renamed.
        var inserted = await CallAsync(client, "atlas_insert_section", new
        {
            pageId = draftId, expectedVersion = 1, heading = "Kontrola", level = 2, markdown = "Zkontrolujte připojení.", afterHeadingId = (string?)null
        });
        Assert.True(inserted.IsError != true, Text(inserted));
        var page = Structured(await CallAsync(client, "atlas_get_page", new { pageId = draftId, version = "working" }));
        Assert.Equal(["Nový postup", "Kroky", "Kontrola"], page["sections"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()));
        Assert.True(Guid.TryParse(page["sections"]![2]!["headingId"]!.GetValue<string>(), out _));
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "atlas_insert_section", new
        {
            pageId = draftId, expectedVersion = 2, heading = "Špatně", level = 2, afterHeadingId = "x", position = "start"
        })));
        var renamed = await CallAsync(client, "atlas_rename_page", new { pageId = draftId, expectedVersion = 2, title = "Postup připojení" });
        Assert.Equal(3, Structured(renamed)["version"]!.GetValue<int>());
        Assert.Equal("Postup připojení", await CurrentAsync<string>(f, draftId, "Title"));

        // A page without components may be replaced as a whole; unchanged headings keep their ids, an H5 stays level 5.
        var kroky = page["sections"]![1]!["headingId"]!.GetValue<string>();
        // A wrongly typed integer is the caller's input (400 invalid_argument naming it), not a 500 from the live-write wrapper.
        // A numeric string ("1") binds as its number under the web JSON defaults, so it is no wrong type.
        foreach (object? wrong in new object?[] { "x", "1.5", null, new { }, new[] { 1 } })
        {
            var refused = await CallAsync(client, "atlas_replace_page", new { pageId = draftId, expectedVersion = wrong, markdown = "# X\n" });
            Assert.Equal("invalid_argument", ErrorCode(refused));
            Assert.Contains("expectedVersion", Text(refused));
        }
        var replaced =await CallAsync(client, "atlas_replace_page", new
        {
            pageId = draftId, expectedVersion = 3, markdown = "# Nový postup\n\nPřepsaný úvod.\n\n## Kroky\n\n1. Připojit\n2. Ověřit\n\n##### Detail\n\nPodrobnost.\n"
        });
        Assert.True(replaced.IsError != true, Text(replaced));
        // The section "Kontrola" is left out, so its heading id is reported as gone; nothing else warns.
        var kontrola = page["sections"]![2]!["headingId"]!.GetValue<string>();
        var warning = Assert.Single(Structured(replaced)["warnings"]!.AsArray());
        Assert.Contains(kontrola, warning!.GetValue<string>(), StringComparison.Ordinal);
        var read = await CallAsync(client, "atlas_get_page", new { pageId = draftId, version = "working" });
        var after = Structured(read);
        Assert.Equal(kroky, after["sections"]![1]!["headingId"]!.GetValue<string>());
        Assert.Equal([1, 2, 5], after["sections"]!.AsArray().Select(x => x!["level"]!.GetValue<int>()));
        Assert.Contains("##### Detail", Text(read));
        Assert.Matches(@"""level"":\s*5", await CurrentAsync<string>(f, draftId, "ContentJson"));

        // From a template: its copy, then the text after it, in one write.
        var fromTemplate = await CallAsync(client, "atlas_create_draft", new
        {
            spaceId = space, title = "Postup podle šablony", templatePageId = template["id"]!.GetValue<string>(), markdown = "Doplněný krok."
        });
        Assert.True(fromTemplate.IsError != true, Text(fromTemplate));
        var copy = Structured(fromTemplate)["pageId"]!.GetValue<string>();
        var copied = Text(await CallAsync(client, "atlas_get_page", new { pageId = copy, version = "working" }));
        Assert.True(copied.IndexOf("Krok ze šablony.", StringComparison.Ordinal) < copied.IndexOf("Doplněný krok.", StringComparison.Ordinal), copied);
        Assert.Equal(DBNull.Value, await f.Site.ScalarAsync("SELECT \"PublishedRevisionId\" FROM \"Page\" WHERE \"Id\"=@page", ("page", Guid.Parse(copy))) ?? DBNull.Value);
        Assert.Equal(0L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageRevision\" WHERE \"PageId\"=@page AND \"Channel\"<>'mcp'", ("page", Guid.Parse(copy))));
    }

    [PostgreSqlFact]
    public async Task Live_edits_are_protected_and_survive()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var (editor, editorId) = await f.UserAsync("editor", level: null);
        await f.GrantAsync(space, editorId, "Writer");
        var shared = await PageAsync(f.Admin, space, "Sdílená", Rich(Heading(2, "a", "A"), Paragraph("Text.")), "Text.");
        var (_, client) = await DraftClientAsync(f);
        var update = new { pageId = shared.Id, expectedVersion = shared.Version, headingId = "a", markdown = "## A\n\nZměna přes MCP.\n" };

        // Two browsers with unsaved edits: refused, without naming anyone, and their log stays.
        var document = await OpenAsync(f.Admin, shared.Id);
        Assert.Equal(document, await OpenAsync(editor, shared.Id));
        await AppendAsync(f, document, editorId);
        await AppendAsync(f, document, f.AdminId);
        var refused = await CallAsync(client, "atlas_update_section", update);
        Assert.Equal("collaboration_unsaved", ErrorCode(refused));
        Assert.DoesNotContain("editor", Text(refused), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2L, await UpdatesAsync(f, shared.Id));
        Assert.Equal("web", await CurrentAsync<string>(f, shared.Id, "Channel"));

        // The owner alone in a browser: the agent must not discard what the owner is typing either.
        var own = await PageAsync(f.Admin, space, "Moje", Rich(Heading(2, "a", "A"), Paragraph("Text.")), "Text.");
        await AppendAsync(f, await OpenAsync(f.Admin, own.Id), f.AdminId);
        Assert.Equal("collaboration_unsaved", ErrorCode(await CallAsync(client, "atlas_update_section", update with { pageId = own.Id, expectedVersion = own.Version })));
        Assert.Equal("collaboration_unsaved", ErrorCode(await CallAsync(client, "atlas_replace_page", new { pageId = own.Id, expectedVersion = own.Version, markdown = "Nové." })));
        Assert.Equal(1L, await UpdatesAsync(f, own.Id));

        // An open session without unsaved edits: the write passes and the generation moves, so live editors reload the new version.
        var quiet = await PageAsync(f.Admin, space, "Klidná", Rich(Heading(2, "a", "A"), Paragraph("Text.")), "Text.");
        await OpenAsync(f.Admin, quiet.Id);
        var before = await f.Site.ScalarAsync("SELECT \"Generation\" FROM \"PageCollaboration\" WHERE \"PageId\"=@page", ("page", Guid.Parse(quiet.Id)));
        var written = await CallAsync(client, "atlas_update_section", update with { pageId = quiet.Id, expectedVersion = quiet.Version });
        Assert.True(written.IsError != true, Text(written));
        var generation = await f.Site.ScalarAsync("SELECT \"Generation\" FROM \"PageCollaboration\" WHERE \"PageId\"=@page", ("page", Guid.Parse(quiet.Id)));
        Assert.NotEqual(before, generation);
    }

    [PostgreSqlFact]
    public async Task Concurrent_save_between_read_and_write_is_a_version_conflict()
    {
        var interleave = new InterleavedHeadingIds();
        await using var f = await McpSite.CreateAsync(services: services => services.Replace(ServiceDescriptor.Singleton<McpHeadingIds>(interleave)));
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Souběh", Rich(Heading(2, "a", "A"), Paragraph("Text.")), "Text.");
        var (tokenId, client) = await DraftClientAsync(f);
        // The web saves while the tool converts, after its read and before its write.
        var webVersion = 0;
        interleave.Between = () => webVersion = SaveAsync(f.Admin, page.Id, page.Version, "Souběh", Rich(Heading(2, "a", "A"), Paragraph("Z webu.")), "Z webu.")
            .GetAwaiter().GetResult();

        var conflict = await CallAsync(client, "atlas_insert_section", new { pageId = page.Id, expectedVersion = page.Version, heading = "Nová", level = 2, markdown = "MCP." });
        Assert.Equal("version_conflict", ErrorCode(conflict));
        Assert.Equal(page.Version + 1, webVersion);
        Assert.Equal(webVersion, Structured(conflict)["currentVersion"]!.GetValue<int>());
        Assert.Contains("version: \"working\"", Text(conflict));
        // Nothing of the tool was written: the current revision is the web's.
        Assert.Equal("web", await CurrentAsync<string>(f, page.Id, "Channel"));
        Assert.DoesNotContain("MCP.", await CurrentAsync<string>(f, page.Id, "Markdown"));
        Assert.Equal(["atlas_insert_section:version_conflict:"], await AuditAsync(f, tokenId));
    }

    [PostgreSqlFact]
    public async Task Read_token_cannot_write_and_content_rights_still_apply()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Pravidla", Rich(Heading(2, "a", "A"), Paragraph("Text.")), "Text.");
        var (readerClient, readerId) = await f.UserAsync("reader", "write");
        await f.GrantAsync(space, readerId, "Reader");
        var pages = await f.Site.ScalarAsync("SELECT count(*) FROM \"Page\" WHERE \"SpaceId\"=@space", ("space", Guid.Parse(space)));
        await f.AdminTokenAsync("read");

        // A read token sees no draft tool and calling one is the tool error scope_missing, audited.
        var (readTokenId, readToken) = await IssueAsync(f.Admin, f.Site.Password, ["read"], ["mcp"]);
        var reader = await f.ClientAsync(readToken);
        Assert.DoesNotContain(await reader.ListToolsAsync(), tool => DraftTools.Contains(tool.Name));
        Assert.Equal("scope_missing", ErrorCode(await CallAsync(reader, "atlas_update_section",
            new { pageId = page.Id, expectedVersion = page.Version, headingId = "a", markdown = "## A\n\nX.\n" })));
        Assert.Equal(["atlas_update_section:scope_missing:"], await AuditAsync(f, readTokenId));
        Assert.Equal(Guid.Parse(page.Id), await f.Site.ScalarAsync("SELECT \"PageId\" FROM \"ApiActivity\" WHERE \"TokenId\"=@token", ("token", Guid.Parse(readTokenId))));

        // A draft token of a space reader: the tools are listed, but the content rights are the web's: no EditContent, no write.
        var drafter = await f.ClientAsync((await IssueAsync(readerClient, f.Site.Password, ["read", "write"], ["mcp"])).Secret);
        var listed = (await drafter.ListToolsAsync()).Where(tool => DraftTools.Contains(tool.Name)).ToArray();
        Assert.Equal(DraftTools, listed.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.All(listed, tool => Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint));
        Assert.Equal(["atlas_rename_page", "atlas_update_section"],
            listed.Where(tool => tool.ProtocolTool.Annotations?.IdempotentHint == true).Select(tool => tool.Name).Order(StringComparer.Ordinal));
        // As REST: a page it may read answers 403, a root page in a space it only reads 403 too.
        foreach (var (tool, arguments, code) in new (string, object, string)[]
        {
            ("atlas_update_section", new { pageId = page.Id, expectedVersion = page.Version, headingId = "a", markdown = "## A\n\nX.\n" }, "forbidden"),
            ("atlas_replace_page", new { pageId = page.Id, expectedVersion = page.Version, markdown = "X." }, "forbidden"),
            ("atlas_insert_section", new { pageId = page.Id, expectedVersion = page.Version, heading = "B", level = 2 }, "forbidden"),
            ("atlas_rename_page", new { pageId = page.Id, expectedVersion = page.Version, title = "Jinak" }, "forbidden"),
            ("atlas_create_draft", new { spaceId = space, title = "Nová" }, "forbidden"),
        })
        {
            Assert.Equal(code, ErrorCode(await CallAsync(drafter, tool, arguments)));
        }
        Assert.Equal(1L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageRevision\" WHERE \"PageId\"=@page", ("page", Guid.Parse(page.Id))));
        Assert.Equal(pages, await f.Site.ScalarAsync("SELECT count(*) FROM \"Page\" WHERE \"SpaceId\"=@space", ("space", Guid.Parse(space))));
    }

    [PostgreSqlFact]
    public async Task Components_come_back_from_their_placeholders_and_tampering_is_refused()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        // A page reference: an inline component the dialect leaves a placeholder (a contact is Markdown of its own, a-contact).
        const string contact = """{"type":"pageReference","attrs":{"id":"5e940f4d-55c2-4527-addd-10f1d68d742c","title":"Jan Kontaktní","url":"#/pages/5e940f4d-55c2-4527-addd-10f1d68d742c"}}""";
        const string issue = """{"type":"youtrackIssue","attrs":{"id":"2-12","key":"ABC-12","title":"Opravit přihlášení","url":"https://youtrack.example.com/issue/ABC-12","state":"Open"}}""";
        var page = await PageAsync(f.Admin, space, "Kontakty", Rich(Heading(2, "kontakty", "Kontakty"),
            """{"type":"paragraph","content":[{"type":"text","text":"Volejte "},""" + contact + """,{"type":"text","text":" kvůli "},""" + issue + "]}"),
            "Volejte.", publish: false);
        var (tokenId, client) = await DraftClientAsync(f);
        var read = await CallAsync(client, "atlas_get_page", new { pageId = page.Id, version = "working", headingId = "kontakty" });
        Assert.Contains("Volejte ⟦c:1⟧ kvůli ⟦c:2⟧", Text(read));

        var whole = await CallAsync(client, "atlas_replace_page", new { pageId = page.Id, expectedVersion = page.Version, markdown = "## Kontakty\n\nVolejte ⟦c:3⟧.\n" });
        Assert.Equal("components_changed", ErrorCode(whole));

        // Placeholders tampered with (dropped, swapped, invented) or a password directive: refused, nothing stored content in the error.
        foreach (var (markdown, code) in new[]
        {
            ("## Kontakty\n\nVolejte kvůli ⟦c:2⟧.\n", "components_changed"),
            ("## Kontakty\n\nVolejte ⟦c:2⟧ kvůli ⟦c:1⟧.\n", "components_changed"),
            ("## Kontakty\n\nVolejte ⟦c:1⟧ kvůli ⟦c:2⟧ a ⟦c:3⟧.\n", "components_changed"),
            ("## Kontakty\n\nVolejte ⟦c:1⟧ kvůli ⟦c:2⟧.\n\n::secret{secretId=\"" + Guid.NewGuid() + "\"}\n", "secret_directive_forbidden"),
        })
        {
            var refused = await CallAsync(client, "atlas_update_section", new { pageId = page.Id, expectedVersion = page.Version, headingId = "kontakty", markdown });
            Assert.Equal(code, ErrorCode(refused));
            Assert.DoesNotContain("Jan Kontaktní", Text(refused));
            Assert.DoesNotContain("ABC-12", Text(refused));
        }
        Assert.Equal(page.Version, (int)(await f.Site.ScalarAsync("SELECT \"Version\" FROM \"Page\" WHERE \"Id\"=@page", ("page", Guid.Parse(page.Id))))!);

        // The text around them may change; the components come back byte for byte from the stored revision.
        var edited = await CallAsync(client, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = page.Version, headingId = "kontakty", markdown = "## Kontakty\n\nV noci volejte ⟦c:1⟧ kvůli ⟦c:2⟧.\n"
        });
        Assert.True(edited.IsError != true, Text(edited));
        var stored = JsonNode.Parse(await CurrentAsync<string>(f, page.Id, "ContentJson"))!["document"]!["content"]![1]!["content"]!.AsArray();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(contact), stored.Single(x => x!["type"]!.GetValue<string>() == "pageReference")));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(issue), stored.Single(x => x!["type"]!.GetValue<string>() == "youtrackIssue")));
        Assert.Equal("V noci volejte ", stored[0]!["text"]!.GetValue<string>());

        // The whole page without its placeholders: refused unless removeComponents lists them; then removed and named.
        var unlisted = await CallAsync(client, "atlas_replace_page", new
        {
            pageId = page.Id, expectedVersion = Structured(edited)["version"]!.GetValue<int>(), markdown = "## Kontakty\n\nBez komponent.\n", removeComponents = new[] { 1 }
        });
        Assert.Equal("components_removed", ErrorCode(unlisted));
        Assert.Equal("[2]", Structured(unlisted)["components"]!.ToJsonString());
        var removed = await CallAsync(client, "atlas_replace_page", new
        {
            pageId = page.Id, expectedVersion = Structured(edited)["version"]!.GetValue<int>(), markdown = "## Kontakty\n\nBez komponent.\n", removeComponents = new[] { 1, 2 }
        });
        Assert.True(removed.IsError != true, Text(removed));
        Assert.Contains("Odebrané komponenty: ⟦c:1⟧", Text(removed), StringComparison.Ordinal);
        Assert.DoesNotContain("pageReference", await CurrentAsync<string>(f, page.Id, "ContentJson"), StringComparison.Ordinal);
        var outcomes = await AuditAsync(f, tokenId);
        Assert.Equal(["components_changed", "components_changed", "components_changed", "components_changed", "secret_directive_forbidden", "ok", "components_removed", "ok"],
            outcomes.Select(x => x.Split(':')[1]));
    }

    [PostgreSqlFact]
    public async Task A_section_writes_contacts_and_charts_in_the_dialect_and_an_invalid_one_is_refused_with_its_position()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Kontakty", Rich(Heading(2, "kontakty", "Kontakty"), Paragraph("Volejte.")), "Volejte.", publish: false);
        var (_, client) = await DraftClientAsync(f);
        // A phone and an e-mail of this test's own: the directory is the whole site's, and a strong match would use another test's contact.
        var phone = $"+420 777 {Random.Shared.Next(1_000_000):000 000}";
        var email = $"petr-{Word()}@x.cz";
        var section = $"## Kontakty\n\nVolejte [Jana](a-contact:?name=Jana&phone={phone.Replace(" ", "%20", StringComparison.Ordinal)}).\n\n```a-contact\n- name: Petr\n  emails: [{email}]\n```\n\n"
            + "| Měsíc | Hovory |\n| --- | --- |\n| Leden | 12 |\n\n{#hovory}\n\n```a-chart\nsource:\n  table: hovory\n  rows: 1-2\n  columns: 1-2\n---\n| Měsíc | Hovory |\n| --- | --- |\n| Leden | 12 |\n```\n";

        // An invalid chart: invalid_document naming its line and column, nothing stored.
        var refused = await CallAsync(client, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = page.Version, headingId = "kontakty", markdown = section.Replace("rows: 1-2", "rows: 1-9", StringComparison.Ordinal),
        });
        Assert.Equal("invalid_document", ErrorCode(refused));
        Assert.Contains("řádek 16, sloupec 1: Neplatná komponenta a-chart: Neplatný odkaz na tabulku.", Text(refused), StringComparison.Ordinal);
        Assert.Equal(page.Version, (int)(await f.Site.ScalarAsync("SELECT \"Version\" FROM \"Page\" WHERE \"Id\"=@page", ("page", Guid.Parse(page.Id))))!);

        var written = await CallAsync(client, "atlas_update_section", new { pageId = page.Id, expectedVersion = page.Version, headingId = "kontakty", markdown = section });
        Assert.True(written.IsError != true, Text(written));
        var blocks = JsonNode.Parse(await CurrentAsync<string>(f, page.Id, "ContentJson"))!["document"]!["content"]!.AsArray();
        Assert.Equal(["heading", "paragraph", "contactCard", "table", "chart", "paragraph"], blocks.Select(x => x!["type"]!.GetValue<string>()));
        // Each contact is linked to the one directory (contacts spec §3): a new person is created there and referenced by objectId.
        var jana = blocks[1]!["content"]![1]!["attrs"]!["data"]!["contacts"]![0]!.AsObject();
        var janaId = Guid.Parse(jana["objectId"]!.GetValue<string>());
        jana.Remove("objectId");
        Assert.Equal("""{"type":"contactInline","attrs":{"data":{"version":1,"contacts":[{"name":"Jana","phones":["PHONE"]}]}}}""".Replace("PHONE", phone, StringComparison.Ordinal),
            blocks[1]!["content"]![1]!.ToJsonString(RichDocument.JsonWrite));
        var contacts = Structured(written)["contacts"]!;
        Assert.Equal([janaId.ToString(), blocks[2]!["attrs"]!["data"]!["contacts"]![0]!["objectId"]!.GetValue<string>()], contacts["created"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Empty(contacts["used"]!.AsArray());
        Assert.Equal("hovory", blocks[3]!["attrs"]!["chartRef"]!.GetValue<string>());
        var cells = blocks[3]!["content"]!.AsArray().SelectMany(row => row!["content"]!.AsArray().Select(cell => cell!["attrs"]!["chartRef"]!.GetValue<string>())).ToList();
        Assert.Equal(JsonSerializer.Serialize(cells.Chunk(2)), blocks[4]!["attrs"]!["data"]!["source"]!["cells"]!.ToJsonString());

        // Read back, the linked chart and its table are placeholders; the contacts are the dialect's text.
        var read = Text(await CallAsync(client, "atlas_get_page", new { pageId = page.Id, version = "working", headingId = "kontakty" }));
        Assert.Contains($"Volejte [Jana](a-contact:?name=Jana&phone={phone.Replace(" ", "%20", StringComparison.Ordinal)}&objectId={janaId}).", read, StringComparison.Ordinal);
        Assert.Contains($"```a-contact\n- name: Petr\n  emails: [{email}]\n  objectId: ", read, StringComparison.Ordinal);
    }

    [PostgreSqlFact]
    public async Task A_look_with_nowhere_to_go_is_refused_until_the_call_resets_formatting()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var content = JsonNode.Parse(RichMarkdownMergeTests.FormattedPage)!.ToJsonString();
        var document = RichDocument.Parse(content);
        var page = await PageAsync(f.Admin, space, "Postup", content, EditorMarkdownWriter.Write(document.Document), publish: false);
        var (_, client) = await DraftClientAsync(f);
        // The page is one section: its heading and everything after it.
        var section = RichMarkdownProjection.Project(document, new ProjectionOptions(page.Version)).Markdown;

        // A colour with nowhere to go: refused until the call resets formatting.
        var narrowed = section.Replace("| Krok | Čas |\n| --- | --- |\n| Zapnout | 5 min |", "| Krok a čas |\n| --- |\n| Zapnout |", StringComparison.Ordinal);
        var refused = await CallAsync(client, "atlas_update_section", new { pageId = page.Id, expectedVersion = page.Version, headingId = "h-postup", markdown = narrowed });
        Assert.Equal("formatting_lost", ErrorCode(refused));
        var reset = await CallAsync(client, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = page.Version, headingId = "h-postup", markdown = narrowed, resetFormatting = true
        });
        Assert.Equal(page.Version + 1, Structured(reset)["version"]!.GetValue<int>());
    }

    [PostgreSqlFact]
    public async Task Excluded_space_is_invisible_for_writes()
    {
        await using var f = await McpSite.CreateAsync();
        var excluded = await f.SpaceAsync("Smlouvy");
        // A title of this test's own: the link below resolves on the administrator's rights, every test's pages of the site.
        var title = "Smlouva " + Word();
        var page = await PageAsync(f.Admin, excluded, title, Rich(Heading(2, "a", "A"), Paragraph("Text.")), "Text.");
        await f.ExcludeFromAiAsync(excluded);
        var (_, client) = await DraftClientAsync(f);

        foreach (var (tool, arguments) in new (string, object)[]
        {
            ("atlas_update_section", new { pageId = page.Id, expectedVersion = page.Version, headingId = "a", markdown = "## A\n\nX.\n" }),
            ("atlas_replace_page", new { pageId = page.Id, expectedVersion = page.Version, markdown = "X." }),
            ("atlas_insert_section", new { pageId = page.Id, expectedVersion = page.Version, heading = "B", level = 2 }),
            ("atlas_rename_page", new { pageId = page.Id, expectedVersion = page.Version, title = "Jinak" }),
        })
        {
            var refused = await CallAsync(client, tool, arguments);
            Assert.Equal("not_found", ErrorCode(refused));
            Assert.Equal(Text(await CallAsync(client, "atlas_get_page", new { pageId = Guid.NewGuid().ToString() })), Text(refused));
        }
        // A draft into the excluded space answers exactly as a space that does not exist.
        var create = await CallAsync(client, "atlas_create_draft", new { spaceId = excluded, title = "Nová" });
        Assert.Equal("not_found", ErrorCode(create));
        Assert.Equal(Text(await CallAsync(client, "atlas_create_draft", new { spaceId = Guid.NewGuid().ToString(), title = "Nová" })), Text(create));
        Assert.Equal(1L, await f.Site.ScalarAsync("SELECT count(*) FROM \"Page\" WHERE \"SpaceId\"=@space", ("space", Guid.Parse(excluded))));
        Assert.Equal(1L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageRevision\" WHERE \"PageId\"=@page", ("page", Guid.Parse(page.Id))));

        // A link to a page of the excluded space is not converted either, and the warning does not tell why.
        var space = await f.SpaceAsync("Provoz");
        var created = await CallAsync(client, "atlas_create_draft", new
        {
            spaceId = space, title = "Odkazy", markdown = $"Viz [[{title}]] a [smlouvu](atlas://pages/{page.Id})."
        });
        Assert.True(created.IsError != true, Text(created));
        var json = await CurrentAsync<string>(f, Structured(created)["pageId"]!.GetValue<string>(), "ContentJson");
        Assert.DoesNotContain(page.Id, json);
        Assert.Equal(2, Structured(created)["warnings"]!.AsArray().Count);
    }

    // ---- helpers ----

    /// <summary>3.6 AI parity of the history: the ids atlas_get_history lists are what naming, a version's steps, restore and undo take.</summary>
    [PostgreSqlFact]
    public async Task History_lists_the_ids_that_name_restore_and_undo_take()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Postup", Rich(Paragraph("První.")), "První.");
        var (_, client) = await DraftClientAsync(f);
        var replaced = await CallAsync(client, "atlas_replace_page", new { pageId = page.Id, expectedVersion = page.Version, markdown = "Druhý.\n" });
        Assert.True(replaced.IsError != true, Text(replaced));
        var version = Structured(replaced)["version"]!.GetValue<int>();

        var history = Items(await CallAsync(client, "atlas_get_history", new { pageId = page.Id }));
        var first = history.Single(item => item!["version"]!.GetValue<int>() == page.Version)!;
        var agent = history[0]!;
        Assert.Equal(await CurrentAsync<Guid>(f, page.Id, "Id"), Guid.Parse(agent["revisionId"]!.GetValue<string>()));
        var steps = Items(await CallAsync(client, "atlas_get_history", new { pageId = page.Id, group = agent["groupId"]!.GetValue<string>() }));
        Assert.Equal(agent["revisionId"]!.GetValue<string>(), Assert.Single(steps)!["revisionId"]!.GetValue<string>());

        var named = await CallAsync(client, "atlas_name_version", new { pageId = page.Id, expectedVersion = version, label = "Před úklidem" });
        Assert.True(named.IsError != true, Text(named));
        Assert.Equal(agent["revisionId"]!.GetValue<string>(), Structured(named)["revisionId"]!.GetValue<string>());
        Assert.Equal("Před úklidem", await CurrentAsync<string>(f, page.Id, "Label"));

        var restored = await CallAsync(client, "atlas_restore_version", new { pageId = page.Id, revisionId = first["revisionId"]!.GetValue<string>(), expectedVersion = version });
        Assert.True(restored.IsError != true, Text(restored));
        Assert.Contains("První.", await CurrentAsync<string>(f, page.Id, "Markdown"));
        var restore = Items(await CallAsync(client, "atlas_get_history", new { pageId = page.Id }))[0]!;
        var undone = await CallAsync(client, "atlas_undo_change", new
        {
            pageId = page.Id, revisionId = restore["revisionId"]!.GetValue<string>(), expectedVersion = Structured(restored)["version"]!.GetValue<int>()
        });
        Assert.True(undone.IsError != true, Text(undone));
        Assert.Contains("Druhý.", await CurrentAsync<string>(f, page.Id, "Markdown"));
    }

    /// <summary>3.6: a bullet list of stamped records written over MCP is a timed list (rich revision 5), the instructions say how to write
    /// one, and a write says that readers see it by the page's „Publikace“, not only after a person publishes.</summary>
    [PostgreSqlFact]
    public async Task Stamped_records_written_over_mcp_are_a_timed_list()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Incident", Rich(Paragraph("Záznamy.")), "Záznamy.");
        var (_, client) = await DraftClientAsync(f);
        Assert.Contains("„- 3. 11. 2026 17:19 — text“", client.ServerInstructions);
        Assert.DoesNotContain("až ji publikuje člověk", client.ServerInstructions);

        var written = await CallAsync(client, "atlas_replace_page", new
        {
            pageId = page.Id, expectedVersion = page.Version, markdown = "Záznamy.\n\n- 3. 11. 2026 17:19 — Urgováno IT\n- 2. 11. 2026 — telefonát\n"
        });
        Assert.True(written.IsError != true, Text(written));
        Assert.Contains("atlas_get_publication", Text(written));
        Assert.DoesNotContain("nepublikuje člověk", Text(written));
        await AssertTimedAsync();

        // The read escapes the stamp as CommonMark needs („3\.“ would otherwise open a numbered list); written back, it stays a timed list.
        var working = await CallAsync(client, "atlas_get_page", new { pageId = page.Id, version = "working" });
        Assert.Contains(@"- 3\. 11. 2026 17:19 — Urgováno IT", Text(working));
        var markdown = Text(working).Split("version=\"working\">\n")[1].Split("\n</wiki-page>")[0] + "\n\nDoplněno.\n";
        var again = await CallAsync(client, "atlas_replace_page", new { pageId = page.Id, expectedVersion = Structured(working)["expectedVersion"]!.GetValue<int>(), markdown });
        Assert.True(again.IsError != true, Text(again));
        await AssertTimedAsync();

        async Task AssertTimedAsync()
        {
            var content = JsonNode.Parse(await CurrentAsync<string>(f, page.Id, "ContentJson"))!;
            var list = content["document"]!["content"]!.AsArray().Single(node => node!["type"]!.GetValue<string>() == "timedList")!;
            Assert.Equal(2, list["content"]!.AsArray().Count);
            Assert.All(list["content"]!.AsArray(), item => Assert.Equal("timedItem", item!["type"]!.GetValue<string>()));
        }
    }

    private static string Word() => string.Concat(Guid.NewGuid().ToString("N")[..10].Select(c => (char)('a' + c % 16)));

    /// <summary>The administrator's token with read and draft on the channel mcp: its id and a connected client.</summary>
    private static async Task<(string TokenId, McpClient Client)> DraftClientAsync(McpSite f)
    {
        await f.AdminTokenAsync("read");
        var (id, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"]);
        return (id, await f.ClientAsync(secret));
    }

    private static async Task<T> CurrentAsync<T>(McpSite f, string pageId, string column)
        => (T)(await f.Site.ScalarAsync($"SELECT \"{column}\" FROM \"PageRevision\" WHERE \"PageId\"=@page AND \"ActiveTo\" IS NULL", ("page", Guid.Parse(pageId))))!;

    /// <summary>The audit rows of a token in order: tool:outcome:version.</summary>
    private static async Task<string[]> AuditAsync(McpSite f, string tokenId)
    {
        var rows = (string?)await f.Site.ScalarAsync("""
            SELECT string_agg("Operation" || ':' || "Outcome" || ':' || coalesce("ResultVersion"::text,''), '|' ORDER BY "Id") FROM "ApiActivity" WHERE "TokenId"=@token
            """, ("token", Guid.Parse(tokenId)));
        return rows?.Split('|') ?? [];
    }

    /// <summary>Opens the page's live document as the cookie client's browser would; returns the document id.</summary>
    private static async Task<string> OpenAsync(HttpClient browser, string pageId)
        => (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(browser, HttpMethod.Post, $"/api/pages/{pageId}/collaboration"), HttpStatusCode.OK))["documentId"]!.GetValue<string>();

    /// <summary>One unsaved live update of a user, as the collaboration hub stores it.</summary>
    private static async Task AppendAsync(McpSite f, string documentId, string userId)
    {
        var store = new PostgresCollaborationStore(f.Site.Services.GetRequiredService<IServiceScopeFactory>());
        await store.AppendUpdateAsync(documentId, Guid.NewGuid(), userId, DateTimeOffset.UtcNow, new byte[] { 1 }, default);
    }

    private static async Task<long> UpdatesAsync(McpSite f, string pageId)
        => (long)(await f.Site.ScalarAsync("SELECT count(*) FROM \"CollaborationUpdate\" WHERE \"PageId\"=@page", ("page", Guid.Parse(pageId))))!;

    /// <summary>Runs <see cref="Between"/> once, when the tool's conversion asks for its first new heading id.</summary>
    private sealed class InterleavedHeadingIds : McpHeadingIds
    {
        public Action? Between { get; set; }

        public override string Next()
        {
            var between = Between;
            Between = null;
            between?.Invoke();
            return base.Next();
        }
    }
}
