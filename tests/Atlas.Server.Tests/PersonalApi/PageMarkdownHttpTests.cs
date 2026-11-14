using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Platform.Content;
using Atlas.Server.Tests.Content;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.PersonalApi;

/// <summary>
/// What HTTP adds to the Markdown writes of the personal API (the merge rules are RichMarkdownMergeTests' and RichEmbedTests', the
/// attachment checks the MCP tests'): the read with format=markdown, the save of that text with its placeholders and images, the
/// look changed on the side, the status of a refused attachment and the audit.
/// </summary>
public sealed class PageMarkdownHttpTests
{
    private static readonly byte[] Pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6Cj8AAAAASUVORK5CYII=");

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Markdown_saves_keep_components_and_image_looks_and_add_images_and_file_links()
    {
        await using var f = await McpSite.CreateAsync(enabled: false);
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Příručka", Rich(Heading(2, "a", "A"), Paragraph("Text.")), "Text.");
        var image = await McpImageToolTests.UploadAsync(f.Admin, page.Id, Pixel, "schema.png", "image");
        var file = await McpImageToolTests.UploadAsync(f.Admin, page.Id, "%PDF-1.4 návod"u8.ToArray(), "navod.pdf", "file");
        // The web editor adds a file card, which Markdown cannot carry.
        var card = $$$$"""{"type":"attachment","attrs":{"data":{"version":1,"name":"navod.pdf","url":"/api/attachments/{{{{file}}}}/download"}}}""";
        var withCard = JsonNode.Parse(Rich(Heading(2, "a", "A"), Paragraph("Text."), card))!;
        withCard["plugins"]!.AsArray().Add(new JsonObject { ["id"] = "attachments", ["version"] = 1 });
        var version = await SaveAsync(f.Admin, page.Id, page.Version, "Příručka", withCard.ToJsonString(), "Text.");
        await f.SetLevelAsync(f.AdminId, "write");
        using var api = Bearer(f, (await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["rest"])).Secret);

        var read = await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/pages/{page.Id}?format=markdown"), HttpStatusCode.OK);
        var source = read["source"]!;
        Assert.Equal("## A\n\nText.\n\n⟦c:1⟧\n", source["markdown"]!.GetValue<string>());
        Assert.Equal("""[{"attachmentId":"FILE","kind":"file","occurrence":1}]""".Replace("FILE", file, StringComparison.Ordinal),
            source["components"]![0]!["embeds"]!.ToJsonString());
        Assert.Null((await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/pages/{page.Id}"), HttpStatusCode.OK))["source"]);

        // The text with the placeholder, a new image and a link to the file: the card comes back as stored.
        var markdown = source["markdown"]!.GetValue<string>() + $"\n![Schéma](/api/attachments/{image}/content)\n\n[Návod](/api/attachments/{file}/download)\n";
        var saved = await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/pages/{page.Id}", new { markdown, expectedVersion = version }), HttpStatusCode.OK);
        Assert.Equal(version + 1, saved["version"]!.GetValue<int>());
        Assert.Equal("Příručka", saved["title"]!.GetValue<string>());
        var blocks = Blocks(saved);
        Assert.Equal(["heading", "paragraph", "attachment", "image", "paragraph"], blocks.Select(x => x!["type"]!.GetValue<string>()));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(card), blocks[2]));
        Assert.Equal("a", blocks[0]!["attrs"]!["headingId"]!.GetValue<string>());

        // The look on the side; the next save that leaves the image's Markdown alone keeps it.
        await AtlasHttpSite.Json(await api.PatchAsJsonAsync($"/api/v1/pages/{page.Id}/embeds/{image}", new { expectedVersion = version + 1, width = 40, align = "right" }),
            HttpStatusCode.OK);
        var again = (await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/pages/{page.Id}?format=markdown"), HttpStatusCode.OK))["source"]!["markdown"]!.GetValue<string>();
        Assert.Equal(markdown.Replace("\n\n\n", "\n\n", StringComparison.Ordinal), again);
        var styled = await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/pages/{page.Id}",
            new { markdown = again.Replace("Text.", "Nový text.", StringComparison.Ordinal), expectedVersion = version + 2, title = "Příručka VPN" }), HttpStatusCode.OK);
        var kept = Blocks(styled)[3]!["attrs"]!["presentation"]!;
        Assert.Equal((40, "right"), (kept["width"]!.GetValue<int>(), kept["align"]!.GetValue<string>()));
        Assert.Equal("Příručka VPN", styled["title"]!.GetValue<string>());

        // The card's placeholder left out without removeComponents: nothing is saved, and the answer names it.
        var unlisted = await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/pages/{page.Id}", new { markdown = "Text.", expectedVersion = version + 3 }),
            HttpStatusCode.Conflict);
        Assert.Equal("components_removed", unlisted["code"]!.GetValue<string>());
        Assert.Contains("⟦c:1⟧ (Příloha: navod.pdf)", unlisted["title"]!.GetValue<string>(), StringComparison.Ordinal);

        // The status of a refused attachment (the rule itself is the MCP tests'): another page's attachment is unknown.
        var other = await PageAsync(f.Admin, space, "Jiná", Rich(Paragraph("Jiná.")), "Jiná.");
        var foreign = await McpImageToolTests.UploadAsync(f.Admin, other.Id, Pixel, "cizi.png", "image");
        Assert.Equal("attachment_not_found", (await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/pages/{page.Id}",
            new { markdown = $"![Cizí](/api/attachments/{foreign}/content)", expectedVersion = version + 3, removeComponents = new[] { 1 }, resetFormatting = true }),
            HttpStatusCode.NotFound))["code"]!.GetValue<string>());

        Assert.Equal($"page.save:ok:{version + 1}|page.embed_update:ok:{version + 2}|page.save:ok:{version + 3}|page.save:components_removed:|page.save:attachment_not_found:",
            await f.Site.ScalarAsync("""
                SELECT string_agg("Operation" || ':' || "Outcome" || ':' || coalesce("ResultVersion"::text,''), '|' ORDER BY "Id")
                FROM "ApiActivity" WHERE "Operation" LIKE 'page.%' AND "ActorId"=@admin::uuid
                """, ("admin", f.AdminId)));
    }

    [PostgreSqlFact]
    public async Task Formulas_and_diagrams_read_as_markdown_and_an_unchanged_one_saves_back_as_its_stored_node()
    {
        await using var f = await McpSite.CreateAsync(enabled: false);
        var space = await f.FirstSpaceAsync();
        const string formula = """{"type":"mathBlock","attrs":{"data":{"version":1,"expression":"E = mc^2"}}}""";
        const string diagram = """{"type":"diagram","attrs":{"data":{"version":1,"source":"graph TD\n  A --> B","caption":""}}}""";
        var content = JsonNode.Parse(Rich(Heading(2, "a", "A"), Paragraph("Text."), formula, diagram, """{"type":"paragraph"}"""))!;
        content["plugins"]!.AsArray().Add(new JsonObject { ["id"] = "math", ["version"] = 1 });
        content["plugins"]!.AsArray().Add(new JsonObject { ["id"] = "diagrams", ["version"] = 2 });
        var page = await PageAsync(f.Admin, space, "Vzorce", content.ToJsonString(), "Text.", publish: false);
        await f.SetLevelAsync(f.AdminId, "write");
        using var api = Bearer(f, (await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["rest"])).Secret);

        var source = (await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/pages/{page.Id}?format=markdown"), HttpStatusCode.OK))["source"]!;
        Assert.Equal("## A\n\nText.\n\n$$\nE = mc^2\n$$\n\n```mermaid\ngraph TD\n  A --> B\n```\n", source["markdown"]!.GetValue<string>());
        Assert.Empty(source["components"]!.AsArray());

        // The formula and the diagram come back unchanged (no components_removed); the new inline formula is the editor's node.
        var markdown = source["markdown"]!.GetValue<string>().Replace("Text.", "Text s $x^2$.", StringComparison.Ordinal);
        var blocks = Blocks(await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/pages/{page.Id}", new { markdown, expectedVersion = page.Version }),
            HttpStatusCode.OK));
        Assert.Equal(["heading", "paragraph", "mathBlock", "diagram", "paragraph"], blocks.Select(x => x!["type"]!.GetValue<string>()));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(formula), blocks[2]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(diagram), blocks[3]));
        Assert.Equal("""{"type":"mathInline","attrs":{"data":{"version":1,"expression":"x^2"}}}""", blocks[1]!["content"]![1]!.ToJsonString());
    }

    [PostgreSqlFact]
    public async Task Contacts_and_charts_read_as_markdown_and_an_invalid_one_is_refused_with_its_position()
    {
        await using var f = await McpSite.CreateAsync(enabled: false);
        var space = await f.FirstSpaceAsync();
        // A phone and an e-mail of this test's own: the directory is the whole site's, and a strong match would use another test's contact.
        var phone = $"+420 777 {Random.Shared.Next(1_000_000):000 000}";
        var email = $"petr-{Guid.NewGuid():N}@x.cz";
        var card = """{"type":"contactCard","attrs":{"data":{"version":1,"contacts":[{"name":"Jana","phones":["PHONE"]}]}}}""".Replace("PHONE", phone, StringComparison.Ordinal);
        const string chart = """{"type":"chart","attrs":{"data":{"version":1,"title":"Tržby","kind":"bar","grid":[["Měsíc","Tržby"],["Leden","12"]],"source":null,"sourceStatus":null}}}""";
        var content = JsonNode.Parse(Rich(Heading(2, "a", "A"), Paragraph("Text."), card, chart, """{"type":"paragraph"}"""))!;
        content["plugins"]!.AsArray().Add(new JsonObject { ["id"] = "charts", ["version"] = 1 });
        var page = await PageAsync(f.Admin, space, "Kontakty", content.ToJsonString(), "Text.", publish: false);
        await f.SetLevelAsync(f.AdminId, "write");
        using var api = Bearer(f, (await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["rest"])).Secret);

        var source = (await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/pages/{page.Id}?format=markdown"), HttpStatusCode.OK))["source"]!;
        Assert.Equal($"## A\n\nText.\n\n```a-contact\n- name: Jana\n  phones: [\"{phone}\"]\n```\n\n```a-chart\ntitle: Tržby\n---\n| Měsíc | Tržby |\n| --- | --- |\n| Leden | 12 |\n```\n",
            source["markdown"]!.GetValue<string>());
        Assert.Empty(source["components"]!.AsArray());

        // An invalid chart: 400 invalid_document with its line and column, nothing saved.
        var invalid = source["markdown"]!.GetValue<string>().Replace("| Leden | 12 |", "| Leden | dvanáct |", StringComparison.Ordinal);
        var refused = await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/pages/{page.Id}", new { markdown = invalid, expectedVersion = page.Version }),
            HttpStatusCode.BadRequest);
        Assert.Equal("invalid_document", refused["code"]!.GetValue<string>());
        Assert.Equal((10, 1), (refused["line"]!.GetValue<int>(), refused["column"]!.GetValue<int>()));
        Assert.Contains("řádek 10, sloupec 1: Neplatná komponenta a-chart", refused["title"]!.GetValue<string>(), StringComparison.Ordinal);

        // The contact and the chart come back unchanged (no components_removed); a new inline contact is the editor's node.
        var markdown = source["markdown"]!.GetValue<string>().Replace("Text.", $"Volejte [Petr](a-contact:?name=Petr&email={email}).", StringComparison.Ordinal);
        var blocks = Blocks(await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/pages/{page.Id}", new { markdown, expectedVersion = page.Version }),
            HttpStatusCode.OK));
        Assert.Equal(["heading", "paragraph", "contactCard", "chart", "paragraph"], blocks.Select(x => x!["type"]!.GetValue<string>()));
        // A Markdown write links its contacts to the directory (contacts spec §3): the same card, now a reference by objectId.
        foreach (var linked in new[] { blocks[2]!["attrs"]!["data"]!["contacts"]![0]!, blocks[1]!["content"]![1]!["attrs"]!["data"]!["contacts"]![0]! })
        {
            Assert.True(Guid.TryParse(linked["objectId"]!.GetValue<string>(), out _));
            linked.AsObject().Remove("objectId");
        }
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(card), blocks[2]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(chart), blocks[3]));
        Assert.Equal("""{"type":"contactInline","attrs":{"data":{"version":1,"contacts":[{"name":"Petr","emails":["EMAIL"]}]}}}""".Replace("EMAIL", email, StringComparison.Ordinal), blocks[1]!["content"]![1]!.ToJsonString(RichDocument.JsonWrite));
    }

    [PostgreSqlFact]
    public async Task A_look_with_nowhere_to_go_is_a_409_until_the_body_resets_formatting()
    {
        await using var f = await McpSite.CreateAsync(enabled: false);
        var space = await f.FirstSpaceAsync();
        var content = JsonNode.Parse(RichMarkdownMergeTests.FormattedPage)!.ToJsonString();
        var page = await PageAsync(f.Admin, space, "Postup", content, EditorMarkdownWriter.Write(RichDocument.Parse(content).Document), publish: false);
        await f.SetLevelAsync(f.AdminId, "write");
        using var api = Bearer(f, (await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["rest"])).Secret);
        var markdown = (await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/pages/{page.Id}?format=markdown"), HttpStatusCode.OK))["source"]!["markdown"]!
            .GetValue<string>();

        // A colour with nowhere to go: refused until the request resets formatting.
        var narrowed = markdown.Replace("| Krok | Čas |\n| --- | --- |\n| Zapnout | 5 min |", "| Krok a čas |\n| --- |\n| Zapnout |", StringComparison.Ordinal);
        var refused = await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/pages/{page.Id}", new { markdown = narrowed, expectedVersion = page.Version }),
            HttpStatusCode.Conflict);
        Assert.Equal(("formatting_lost", "table"), (refused["code"]!.GetValue<string>(), refused["blocks"]![0]!["type"]!.GetValue<string>()));
        await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/pages/{page.Id}", new { markdown = narrowed, expectedVersion = page.Version, resetFormatting = true }),
            HttpStatusCode.OK);
    }

    private static JsonArray Blocks(JsonNode page)
        => JsonNode.Parse(page["contentJson"]!.GetValue<string>())!["document"]!["content"]!.AsArray();

    private static HttpClient Bearer(McpSite f, string secret)
    {
        var client = f.Site.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }
}
