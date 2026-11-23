using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.AiIndex;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Mcp.Tools;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.Vaults;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Mcp;

/// <summary>The read tools (MCP plan C1) through the C# SDK client: what reaches a token, G5, versions, password references, limits.</summary>
public sealed class McpReadToolTests
{
    private static readonly string[] ReadTools =
        ["atlas_download_attachment", "atlas_explain_access", "atlas_find_experts", "atlas_find_mentions", "atlas_find_pages_by_tag", "atlas_get_attachment", "atlas_get_backlinks",
            "atlas_get_board", "atlas_get_contact", "atlas_get_history", "atlas_get_job_run", "atlas_get_labels", "atlas_get_note", "atlas_get_overview", "atlas_get_page", "atlas_get_publication", "atlas_get_relations", "atlas_get_skill_graph", "atlas_get_skills", "atlas_get_space_tree", "atlas_list_appearance",
            "atlas_list_attachments", "atlas_list_contact_duplicates", "atlas_list_entities", "atlas_list_jobs", "atlas_list_notifications", "atlas_list_object_trash", "atlas_list_pages", "atlas_list_personal", "atlas_list_recent_changes", "atlas_list_shared_with_me", "atlas_list_space_contents", "atlas_list_spaces", "atlas_list_trash", "atlas_list_unlinked", "atlas_list_usage", "atlas_read_logs",
            "atlas_recognize", "atlas_search", "atlas_suggest_tags"];

    [PostgreSqlFact]
    public async Task Read_token_lists_only_read_tools()
    {
        await using var f = await McpSite.CreateAsync(services: services => services.AddMcpServer().WithTools<McpProbeTools>());
        var reader = await f.ClientAsync(await f.AdminTokenAsync("read"));
        var listed = await reader.ListToolsAsync();
        Assert.Equal(ReadTools, listed.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.All(listed, tool => Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.All(listed, tool => Assert.Null(tool.ProtocolTool.OutputSchema));
        // Called anyway, a tool outside the token's scopes is a tool error the model can read, not a JSON-RPC failure.
        var refused = await CallAsync(reader, McpProbeTools.DraftProbe);
        Assert.Equal("scope_missing", ErrorCode(refused));
        Assert.Equal("Token nemá rozsah Zápis.", Structured(refused)["message"]!.GetValue<string>());
        Assert.Equal("scope_missing", ErrorCode(await CallAsync(reader, McpProbeTools.Undeclared)));

        var drafter = await f.ClientAsync(await f.AdminTokenAsync("read", "write"));
        Assert.Contains(await drafter.ListToolsAsync(), tool => tool.Name == McpProbeTools.DraftProbe);
        Assert.DoesNotContain(await drafter.ListToolsAsync(), tool => tool.Name == McpProbeTools.Undeclared);
        Assert.Equal("draft probe ran", Text(await CallAsync(drafter, McpProbeTools.DraftProbe)));
        // A lowered ceiling applies to the next call of the same client (T1), worded as on REST.
        await f.SetLevelAsync(f.AdminId, "read");
        Assert.DoesNotContain(await drafter.ListToolsAsync(), tool => tool.Name == McpProbeTools.DraftProbe);
        var lowered = await CallAsync(drafter, McpProbeTools.DraftProbe);
        Assert.Equal("scope_missing", ErrorCode(lowered));
        Assert.Equal("Správce wiki vám snížil oprávnění API; token teď smí jen čtení.", Structured(lowered)["message"]!.GetValue<string>());
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Access_matrix_matches_the_personal_api()
    {
        await using var f = await McpSite.CreateAsync();
        var (_, readerId) = await f.UserAsync("reader");
        var open = await f.SpaceAsync("Provoz");
        var closed = await f.SpaceAsync("Vedení");
        var excluded = await f.SpaceAsync("Smlouvy");
        await f.GrantAsync(open, readerId, "Reader");
        await f.GrantAsync(excluded, readerId, "Reader");
        var readable = await PageAsync(f.Admin, open, "Připojení k VPN", Rich(Paragraph("Postup vpnkoala pro všechny.")), "Postup vpnkoala pro všechny.");
        var child = await PageAsync(f.Admin, open, "Klient VPN", Rich(Paragraph("Instalace vpnkoala klienta.")), "Instalace vpnkoala klienta.", parentId: readable.Id);
        var hidden = await PageAsync(f.Admin, closed, "VPN vedení", Rich(Paragraph("Tajné vpnkoala.")), "Tajné vpnkoala.");
        var outside = await PageAsync(f.Admin, excluded, "VPN smlouvy", Rich(Paragraph("Vyloučené vpnkoala, odkaz na VPN."),
            """{"type":"paragraph","content":[{"type":"pageReference","attrs":{"id":"PAGE","title":"Připojení k VPN","url":"#/pages/PAGE"}}]}""".Replace("PAGE", readable.Id)), "Vyloučené vpnkoala.");
        await f.ExcludeFromAiAsync(excluded);
        var reader = await f.ClientAsync((await IssueAsync(await ReaderSessionAsync(f, readerId), f.Site.Password, ["read"], ["mcp"])).Secret);

        var found = Structured(await CallAsync(reader, "atlas_search", new { query = "vpnkoala" }))["items"]!.AsArray();
        Assert.Equal(new[] { readable.Id, child.Id }.Order(StringComparer.Ordinal),
            found.Select(x => x!["pageId"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Equal(f.SpaceName(open) + " › Připojení k VPN › Klient VPN", found.Single(x => x!["pageId"]!.GetValue<string>() == child.Id)!["path"]!.GetValue<string>());
        Assert.Empty(Structured(await CallAsync(reader, "atlas_search", new { query = "vpnkoala", spaceId = excluded }))["items"]!.AsArray());

        var spaces = Structured(await CallAsync(reader, "atlas_list_spaces"))["items"]!.AsArray().Select(x => x!["spaceId"]!.GetValue<string>()).ToArray();
        Assert.Contains(open, spaces);
        Assert.DoesNotContain(closed, spaces);
        Assert.DoesNotContain(excluded, spaces);

        var page = await CallAsync(reader, "atlas_get_page", new { pageId = readable.Id });
        Assert.True(page.IsError != true, Text(page));
        Assert.Contains("Postup vpnkoala pro všechny.", Text(page));
        // Clients that show the model only the structured content (Claude Code) still get the page text with its trust notice.
        Assert.Equal(Text(page), Structured(page)["text"]!.GetValue<string>());
        var root =Structured(await CallAsync(reader, "atlas_list_pages", new { spaceId = open }))["items"]!.AsArray();
        var listed = Assert.Single(root)!;
        Assert.Equal(readable.Id, listed["pageId"]!.GetValue<string>());
        Assert.True(listed["hasChildren"]!.GetValue<bool>());
        Assert.Null(listed["hasUnsavedLiveEdits"]);
        Assert.Equal(child.Id, Assert.Single(Structured(await CallAsync(reader, "atlas_list_pages", new { spaceId = open, parentId = readable.Id }))["items"]!.AsArray())!["pageId"]!.GetValue<string>());

        // Unreadable and excluded answer the same 404, and an excluded space leaves no trace in any list.
        foreach (var (tool, arguments) in new (string, object)[]
        {
            ("atlas_get_page", new { pageId = hidden.Id }), ("atlas_get_page", new { pageId = outside.Id }),
            ("atlas_get_page", new { pageId = Guid.NewGuid().ToString() }),
            ("atlas_list_pages", new { spaceId = closed }), ("atlas_list_pages", new { spaceId = excluded }),
            ("atlas_get_backlinks", new { pageId = outside.Id }), ("atlas_get_history", new { pageId = outside.Id })
        })
        {
            var refused = await CallAsync(reader, tool, arguments);
            Assert.Equal("not_found", ErrorCode(refused));
            Assert.DoesNotContain("VPN smlouvy", Text(refused));
            Assert.DoesNotContain("VPN vedení", Text(refused));
        }
        // The excluded page links to the readable one; the backlink does not show it.
        var admin = await f.ClientAsync(await f.AdminTokenAsync("read"));
        Assert.Empty(Structured(await CallAsync(admin, "atlas_get_backlinks", new { pageId = readable.Id }))["items"]!.AsArray());
        Assert.Equal("not_found", ErrorCode(await CallAsync(admin, "atlas_get_page", new { pageId = outside.Id })));
        // The refused read of the excluded page left no opening in the access history.
        Assert.Equal(0L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageAccessEvent\" WHERE \"PageId\"=@page AND \"Channel\"='mcp'", ("page", Guid.Parse(outside.Id))));
        Assert.Equal(1L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageAccessEvent\" WHERE \"PageId\"=@page AND \"Channel\"='mcp'", ("page", Guid.Parse(readable.Id))));
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(reader, "atlas_get_page", new { pageId = "not-a-page" })));
    }

    [PostgreSqlFact]
    public async Task Unpublished_page_of_an_excluded_space_is_not_found_like_an_unknown_id()
    {
        await using var f = await McpSite.CreateAsync();
        var excluded = await f.SpaceAsync("Smlouvy");
        var draft = await PageAsync(f.Admin, excluded, "Koncept smlouvy", Rich(Paragraph("Text.")), "Text.", publish: false);
        var open = await f.SpaceAsync("Provoz");
        var unpublished = await PageAsync(f.Admin, open, "Koncept", Rich(Paragraph("Text.")), "Text.", publish: false);
        await f.ExcludeFromAiAsync(excluded);
        var editor = await f.ClientAsync(await f.AdminTokenAsync("read"));

        // The positive control: the editor of a reachable unpublished page does get not_published for the default version.
        Assert.Equal("not_published", ErrorCode(await CallAsync(editor, "atlas_get_page", new { pageId = unpublished.Id })));
        var refused = await CallAsync(editor, "atlas_get_page", new { pageId = draft.Id });
        var unknown = await CallAsync(editor, "atlas_get_page", new { pageId = Guid.NewGuid().ToString() });
        Assert.Equal("not_found", ErrorCode(refused));
        Assert.Equal(Structured(unknown).ToJsonString(), Structured(refused).ToJsonString());
    }

    [PostgreSqlFact]
    public async Task Get_page_defaults_to_published_and_working_needs_edit()
    {
        await using var f = await McpSite.CreateAsync();
        var (_, readerId) = await f.UserAsync("reader");
        var space = await f.SpaceAsync("Provoz");
        await f.GrantAsync(space, readerId, "Reader");
        var published = await PageAsync(f.Admin, space, "Postup", Rich(Heading(2, "uvod", "Úvod"), Paragraph("Publikovaný text.")), "Publikovaný text.");
        var working = await SaveAsync(f.Admin, published.Id, published.Version, "Postup",
            Rich(Heading(2, "uvod", "Úvod"), Paragraph("Pracovní text."), Heading(2, "dalsi", "Další"), Paragraph("Nová sekce.")), "Pracovní text.");
        var editor = await f.ClientAsync(await f.AdminTokenAsync("read"));
        var reader = await f.ClientAsync((await IssueAsync(await ReaderSessionAsync(f, readerId), f.Site.Password, ["read"], ["mcp"])).Secret);

        var byDefault = await CallAsync(editor, "atlas_get_page", new { pageId = published.Id });
        Assert.Contains("Publikovaný text.", Text(byDefault));
        Assert.DoesNotContain("Pracovní text.", Text(byDefault));
        Assert.Equal("published", Structured(byDefault)["version"]!.GetValue<string>());
        Assert.Equal(working, Structured(byDefault)["expectedVersion"]!.GetValue<int>());
        Assert.Contains("<wiki-page id=\"" + published.Id + "\" version=\"published\">", Text(byDefault));

        var draft = await CallAsync(editor, "atlas_get_page", new { pageId = published.Id, version = "working" });
        Assert.Contains("Pracovní text.", Text(draft));
        Assert.Equal(working, Structured(draft)["expectedVersion"]!.GetValue<int>());
        var section = await CallAsync(editor, "atlas_get_page", new { pageId = published.Id, version = "working", headingId = "dalsi" });
        Assert.Contains("Nová sekce.", Text(section));
        Assert.DoesNotContain("Pracovní text.", Text(section));
        Assert.Equal("heading_not_found", ErrorCode(await CallAsync(editor, "atlas_get_page", new { pageId = published.Id, headingId = "dalsi" })));

        var forReader = await CallAsync(reader, "atlas_get_page", new { pageId = published.Id });
        Assert.Contains("Publikovaný text.", Text(forReader));
        Assert.Null(Structured(forReader)["expectedVersion"]);
        Assert.DoesNotContain("expectedVersion", Text(forReader));
        // 404 without telling a reader that a working revision exists.
        Assert.Equal("not_found", ErrorCode(await CallAsync(reader, "atlas_get_page", new { pageId = published.Id, version = "working" })));
        // The history of a page its reader reads is forbidden, not hidden (review S3): the page is no secret to them.
        Assert.Equal("forbidden", ErrorCode(await CallAsync(reader, "atlas_get_history", new { pageId = published.Id })));
        var history = Structured(await CallAsync(editor, "atlas_get_history", new { pageId = published.Id }))["items"]!.AsArray();
        Assert.Equal("web", history[0]!["channel"]!.GetValue<string>());

        // A page longer than one result pages at section boundaries; the parts add up to the whole page.
        var blocks = Enumerable.Range(1, 40).SelectMany(n => new[] { Heading(2, $"s{n}", $"Sekce {n}"), Paragraph($"Odstavec {n} " + new string('x', 1_500)) }).ToArray();
        var longPage = await PageAsync(f.Admin, space, "Dlouhá stránka", Rich(blocks), "Dlouhá");
        var parts = new StringBuilder();
        string? cursor = null;
        var calls = 0;
        do
        {
            var part = await CallAsync(editor, "atlas_get_page", new { pageId = longPage.Id, cursor });
            Assert.True(part.IsError != true, Text(part));
            Assert.True(Text(part).Length <= McpText.Limit, Text(part).Length.ToString());
            var text = Text(part);
            var start = text.IndexOf("version=\"published\">\n", StringComparison.Ordinal) + "version=\"published\">\n".Length;
            parts.Append(text[start..text.IndexOf("</wiki-page>", start, StringComparison.Ordinal)].TrimEnd('\n')).Append('\n');
            cursor = Structured(part)["nextCursor"]?.GetValue<string>();
            calls++;
        } while (cursor is not null && calls < 10);
        Assert.InRange(calls, 2, 5);
        for (var n = 1; n <= 40; n++)
        {
            Assert.Contains($"## Sekce {n}\n", parts.ToString());
        }
    }

    [PostgreSqlFact]
    public async Task Secret_references_are_opaque_slots()
    {
        const string fixture = "argon2d-aes-gzip.kdbx";
        var folder = Path.Combine(Path.GetTempPath(), "atlas-mcp-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using var f = await McpSite.CreateAsync(settings: settings => settings["Vaults:RefreshSeconds"] = "0");
            var spaceId = await f.FirstSpaceAsync();
            var location = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/vault-locations",
                new { name = TestLogin.For("Místní"), kind = "local", root = folder }), HttpStatusCode.OK);
            File.WriteAllBytes(Path.Combine(folder, "provoz.kdbx"), KdbxTestFiles.Read(fixture));
            var vault = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/vaults/attach", new
            {
                spaceId, locationId = location["id"]!.GetValue<string>(), name = "Provoz", fileName = "provoz.kdbx", password = KdbxTestFiles.Password(fixture)
            }), HttpStatusCode.OK);
            var router = (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/vaults/{vault["id"]!.GetValue<string>()}/secrets"), HttpStatusCode.OK)).AsArray()
                .Single(s => s!["title"]!.GetValue<string>() == "Router")!["secretId"]!.GetValue<string>();
            var content = SecretReferenceGuardTests.Rich(Paragraph("Přihlášení k routeru:"), SecretReferenceGuardTests.Reference(Guid.Parse(router)));
            var page = await PageAsync(f.Admin, spaceId, "Router", content, "Přihlášení k routeru");

            var client = await f.ClientAsync(await f.AdminTokenAsync("read"));
            var result = await CallAsync(client, "atlas_get_page", new { pageId = page.Id });
            var text = Text(result) + result.StructuredContent?.GetRawText();
            Assert.True(result.IsError != true, text);
            Assert.Contains("⟦c:1⟧", text);
            Assert.Contains("Odkaz na heslo", text);
            Assert.DoesNotContain(router, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("::secret", text);
            Assert.DoesNotContain(":secret", text);
            Assert.DoesNotContain("Provoz\"", text);
            foreach (var canary in VaultHttpTests.Canaries(fixture).Append(KdbxTestFiles.Password(fixture)))
            {
                Assert.DoesNotContain(canary, text, StringComparison.Ordinal);
            }
            Assert.Equal("secretReference", Structured(result)["components"]![0]!["type"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [PostgreSqlFact]
    public async Task Search_is_published_only_and_rate_limited_with_retry_after()
    {
        // The REST "search" budget (30), without replenishment during the test: the 31st search of the token is refused.
        await using var f = await McpSite.CreateAsync(services: services => services.Replace(ServiceDescriptor.Singleton(new McpSearchLimiter(
            new TokenBucketRateLimiterOptions { TokenLimit = 30, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromHours(1), AutoReplenishment = true, QueueLimit = 0 }))));
        var spaceId = await f.FirstSpaceAsync();
        var published = await PageAsync(f.Admin, spaceId, "Zvířata pandakoala", Rich(Paragraph("Publikovaná pandakoala.")), "Publikovaná pandakoala.");
        var draft = await PageAsync(f.Admin, spaceId, "Koncept pandakoala", Rich(Paragraph("Nepublikovaná pandakoala.")), "Nepublikovaná pandakoala.", publish: false);
        var client = await f.ClientAsync(await f.AdminTokenAsync("read"));

        var first = await CallAsync(client, "atlas_search", new { query = "pandakoala" });
        var ids = Structured(first)["items"]!.AsArray().Select(x => x!["pageId"]!.GetValue<string>()).ToArray();
        Assert.Equal([published.Id], ids);
        Assert.DoesNotContain(draft.Id, Text(first));
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "atlas_search", new { query = "pandakoala", limit = 21 })));
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "atlas_search", new { query = "pandakoala", cursor = "bm90LWEtY3Vyc29y" })));
        for (var n = 2; n <= 30; n++)
        {
            Assert.NotEqual(true, (await CallAsync(client, "atlas_search", new { query = "pandakoala" })).IsError);
        }
        var limited = await CallAsync(client, "atlas_search", new { query = "pandakoala" });
        Assert.Equal("rate_limited", ErrorCode(limited));
        Assert.Equal(429, Structured(limited)["status"]!.GetValue<int>());
        Assert.True(Structured(limited)["retryAfter"]!.GetValue<int>() > 0);
        // Other read tools keep working: the search budget is its own.
        Assert.NotEqual(true, (await CallAsync(client, "atlas_get_page", new { pageId = published.Id })).IsError);
    }

    [PostgreSqlFact]
    public async Task Works_without_ai_configuration()
    {
        await using var f = await McpSite.CreateAsync();
        var spaceId = await f.FirstSpaceAsync();
        // Words of this test's own: the wiki administrator's search reads every test's pages on the shared site.
        var word = Word();
        var page = await PageAsync(f.Admin, spaceId, "Bez AI", Rich(Paragraph($"Lexikální {word}.")), $"Lexikální {word}.");
        var client = await f.ClientAsync(await f.AdminTokenAsync("read"));
        var found = await CallAsync(client, "atlas_search", new { query = word });
        Assert.Equal(page.Id, Assert.Single(Structured(found)["items"]!.AsArray())!["pageId"]!.GetValue<string>());
        // S2-3: a page with only some of the words is found by the fallback, and the model is told so.
        var some = await CallAsync(client, "atlas_search", new { query = $"{word} {Word()} {Word()} {Word()} {Word()}" });
        Assert.Equal(page.Id, Assert.Single(Structured(some)["items"]!.AsArray())!["pageId"]!.GetValue<string>());
        Assert.True(Structured(some)["relaxed"]!.GetValue<bool>());
        Assert.Contains("nenalezeno pro všechna slova", Text(some));
        Assert.DoesNotContain("nenalezeno pro všechna slova", Text(found));
        Assert.NotEqual(true, (await CallAsync(client, "atlas_get_page", new { pageId = page.Id })).IsError);
        var ai = (FakeAiClientFactory)f.Site.Services.GetRequiredService<IAiClientFactory>();
        // The fake is the shared site's: only calls carrying this test's word are its own.
        Assert.DoesNotContain(ai.Calls, call => call.Inputs.Any(input => input.Contains(word, StringComparison.Ordinal)));
        Assert.Equal(0, ai.Probes("chat") + ai.Probes("embeddings"));
    }

    [PostgreSqlFact]
    public async Task Tool_arguments_never_reach_the_log()
    {
        await using var f = await McpSite.CreateAsync();
        var spaceId = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, spaceId, "Logovací test", Rich(Paragraph("Obsah TEXTKANAREK stránky.")), "Obsah TEXTKANAREK stránky.");
        var client = await f.ClientAsync(await f.AdminTokenAsync("read"));
        Assert.NotEqual(true, (await CallAsync(client, "atlas_search", new { query = "DOTAZKANAREK" })).IsError);
        Assert.NotEqual(true, (await CallAsync(client, "atlas_get_page", new { pageId = page.Id })).IsError);
        Assert.True((await CallAsync(client, "atlas_get_page", new { pageId = "IDKANAREK" })).IsError);
        Assert.True((await CallAsync(client, "atlas_get_page", new { pageId = page.Id, cursor = "CURSORKANAREK" })).IsError);
        Assert.True((await CallAsync(client, "atlas_get_page", new { pageId = Guid.NewGuid().ToString(), headingId = "NADPISKANAREK" })).IsError);
        var log = string.Join("\n", f.Site.Logs.Messages);
        foreach (var canary in new[] { "DOTAZKANAREK", "TEXTKANAREK", "IDKANAREK", "CURSORKANAREK", "NADPISKANAREK", "Logovací test" })
        {
            Assert.DoesNotContain(canary, log, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// With semantic search available atlas_search is hybrid like /api/v1/search (MCP spec §1), and G5 still holds: a space switched
    /// out of AI leaves MCP with neither its meaning-only hits nor its lexical ones, while the web keeps the lexical one. The fake
    /// model gives the query and both "Připojení zvenku" sections one concept.
    /// </summary>
    [PostgreSqlFact]
    public async Task Hybrid_search_keeps_spaces_excluded_from_ai_out()
    {
        await using var f = await McpSite.CreateAsync(isolated: true); // isolated: the embeddings profile, AI switch and index generation are one setting of the installation, and FakeAiClientFactory.Meaning one fake
        f.Site.Services.GetRequiredService<FakeAiClientFactory>().Meaning = text =>
            text.Contains("vzdálený přístup", StringComparison.OrdinalIgnoreCase) || text.Contains("Připojení zvenku", StringComparison.Ordinal)
                ? "remote access from home"
                : text;
        var included = await f.SpaceAsync("Provoz");
        var excluded = await f.SpaceAsync("Smlouvy");
        var meaning = await PageAsync(f.Admin, included, "Práce z domova", IndexFixture.Rich("Přes klienta a telefon.", ("Připojení zvenku", "zvenku")),
            "## Připojení zvenku\nPřes klienta a telefon.");
        var hiddenMeaning = await PageAsync(f.Admin, excluded, "Brána smluv", IndexFixture.Rich("Přes smluvní bránu.", ("Připojení zvenku", "brana")),
            "## Připojení zvenku\nPřes smluvní bránu.");
        var hiddenWords = await PageAsync(f.Admin, excluded, "Vzdálený přístup ke smlouvám", Rich(Paragraph("Jen pro právníky.")), "Jen pro právníky.");
        await EnableSemanticSearchAsync(f, pages: 3);
        var client = await f.ClientAsync(await f.AdminTokenAsync("read"));

        // Hybrid: the meaning-only page comes with its passage and section; the excluded space's pages still come before G5 applies.
        var before = Items(await CallAsync(client, "atlas_search", new { query = "vzdálený přístup" }));
        Assert.Equal(new[] { meaning.Id, hiddenMeaning.Id, hiddenWords.Id }.Order(StringComparer.Ordinal),
            before.Select(x => x!["pageId"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        var hit = before.Single(x => x!["pageId"]!.GetValue<string>() == meaning.Id)!;
        Assert.Equal(("Přes klienta a telefon.", "zvenku"), (hit["snippet"]!.GetValue<string>(), hit["headingId"]!.GetValue<string>()));

        await f.ExcludeFromAiAsync(excluded);
        var after = await CallAsync(client, "atlas_search", new { query = "vzdálený přístup" });
        Assert.Equal(meaning.Id, Assert.Single(Items(after))!["pageId"]!.GetValue<string>());
        Assert.DoesNotContain("smlouv", Text(after), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Items(await CallAsync(client, "atlas_search", new { query = "vzdálený přístup", spaceId = excluded })));
        // The web loses only the excluded space's meaning hit (G5: "běžné hledání zůstává").
        var web = await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/search?content=published&q=" + Uri.EscapeDataString("vzdálený přístup")), HttpStatusCode.OK);
        Assert.True(web["semantic"]!.GetValue<bool>());
        Assert.Equal(new[] { meaning.Id, hiddenWords.Id }.Order(StringComparer.Ordinal),
            web["items"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    /// <summary>The agent's search gives the text of a hybrid hit's passage (the whole chunk up to 1 500 characters, not the 320 of the web)
    /// within the assistant's output cap; the structured items stay as they were.</summary>
    [PostgreSqlFact]
    public async Task Search_gives_the_passage_text_of_hybrid_hits_within_the_assistant_cap()
    {
        await using var f = await McpSite.CreateAsync(isolated: true); // isolated: as above (embeddings profile, AI switch, index generation, one fake)
        // One concept, near but distinct per page (HNSW folds identical vectors).
        f.Site.Services.GetRequiredService<FakeAiClientFactory>().Meaning = text => !text.Contains("vpnkoala", StringComparison.Ordinal) ? text
            : "remote access from home" + new[] { "Alfa", "Beta", "Gama" }.Where(name => text.Contains(name, StringComparison.Ordinal)).Select(name => " " + name.ToLowerInvariant()).FirstOrDefault();
        var space = await f.SpaceAsync("Provoz");
        var filler = string.Join(' ', Enumerable.Repeat("Postup pro připojení z domova.", 30));
        var pages = new List<string>();
        foreach (var name in new[] { "Alfa", "Beta", "Gama" })
        {
            var body = $"Návod vpnkoala {name}. {filler} KONEC-{name}.";
            pages.Add((await PageAsync(f.Admin, space, name, IndexFixture.Rich(body, ("Postup", "postup")), "## Postup\n" + body)).Id);
        }
        await EnableSemanticSearchAsync(f, pages: 3);
        var client = await f.ClientAsync(await f.AdminTokenAsync("read"));

        var found = await CallAsync(client, "atlas_search", new { query = "vpnkoala" });
        Assert.Equal(pages.Order(StringComparer.Ordinal), Items(found).Select(x => x!["pageId"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Contains("   text: Návod vpnkoala", Text(found));
        Assert.Contains("KONEC-Gama", Text(found));
        Assert.True(Text(found).Length <= Atlas.Server.Features.Assistant.Agent.AssistantToolSession.OutputCap, $"{Text(found).Length} characters");
        Assert.DoesNotContain(Items(found), item => item!["snippet"]!.GetValue<string>().Contains("KONEC", StringComparison.Ordinal));
        // Plan 3.5 I: no heading names the query, so each hit is placed in its passage's section (a requirement block's heading on a zakázka).
        Assert.Contains("   sekce: Postup (headingId: postup)", Text(found));
        Assert.All(Items(found), item => Assert.Equal("postup", item!["headingId"]!.GetValue<string>()));
    }

    [Fact]
    public void Passages_share_the_output_cap_best_hits_first_and_every_hit_keeps_a_short_passage()
    {
        // Plenty of room: each passage whole up to 1 500 characters; a hit without a passage gets nothing.
        Assert.Equal([1500, 800, 0], McpReadTools.PassageLengths([2600, 800, 0], 10_000));
        // Tight: the first hits take what the later ones do not need for their 320 characters.
        Assert.Equal([1500, 1500, 1060, 320, 320], McpReadTools.PassageLengths([2600, 2600, 2600, 2600, 2600], 4700));
        // No room at all still shows the short passage of today.
        Assert.Equal([320, 320], McpReadTools.PassageLengths([2600, 2600], 0));
    }

    /// <summary>Embeddings profile, AI switch, a built, verified and activated index generation, through the admin endpoints.</summary>
    private static async Task EnableSemanticSearchAsync(McpSite f, int pages)
    {
        await AtlasHttpSite.GrantOwnAiAsync(f.Admin);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, "/api/admin/ai/profiles/embeddings", new
        {
            enabled = true, provider = "openai-compatible", location = "company", baseUrl = "http://lmstudio.example:1234/v1", model = "text-embedding-bge-m3",
            dimensions = 1024, timeoutSeconds = 30, maxConcurrency = 4, nativeJsonSchema = true, clearApiKey = false, expectedVersion = (string?)null,
        }), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, "/api/admin/ai/switch", new { enabled = true, expectedVersion = (string?)null }), HttpStatusCode.OK);
        var generation = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/admin/ai/index/generations", new { mode = "build" }), HttpStatusCode.OK);
        var generationId = generation["id"]!.GetValue<string>();
        var runner = f.Site.Services.GetRequiredService<IndexRunner>();
        for (var attempt = 0; ; attempt++)
        {
            // The probe service verifies the new profile at once; a worker round does nothing before that.
            await runner.RunUntilIdleAsync(DateTimeOffset.UtcNow.AddMinutes(1), default);
            var building = (await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/admin/ai/index"), HttpStatusCode.OK))["building"]!;
            if (building["indexedPages"]!.GetValue<int>() >= pages && building["pendingTasks"]!.GetValue<int>() == 0)
            {
                break;
            }
            Assert.True(attempt < 200, "The index was not built.");
            await Task.Delay(50);
        }
        Assert.True((await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/admin/ai/index/generations/{generationId}/verify"),
            HttpStatusCode.OK))["canActivate"]!.GetValue<bool>());
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/admin/ai/index/generations/{generationId}/activate",
            new { expectedVersion = generation["version"]!.GetValue<string>() }), HttpStatusCode.OK);
        for (var attempt = 0; !(await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/ai/capabilities"), HttpStatusCode.OK))["semanticSearch"]!.GetValue<bool>(); attempt++)
        {
            Assert.True(attempt < 200, "Semantic search did not become available.");
            await Task.Delay(50);
        }
    }

    private static string Word() => string.Concat(Guid.NewGuid().ToString("N")[..10].Select(c => (char)('a' + c % 16)));

    /// <summary>A second cookie session of an invited account (UserAsync keeps its own); tokens are issued from it.</summary>
    private static async Task<HttpClient> ReaderSessionAsync(McpSite f, string userId)
    {
        var client = f.Site.Client();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/login", new { username = f.Login(userId), password = f.Site.Password }), HttpStatusCode.OK);
        return client;
    }
}
