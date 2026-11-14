using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.PageHistory.Commands;
using Atlas.Server.Features.PageHistory.Contracts;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.PageSource.Commands;
using Atlas.Server.Features.PageSource.Contracts;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Projector.ProjectorDocuments;

namespace Atlas.Server.Tests.Projector;

/// <summary>Conversion bug 3 (handoff 2026-11-04): a write in the old requirement-block shape (POST /api/pages, PUT, MCP through them, the
/// page source, a restore of history) is stored as requirement headings by the one conversion (RequirementPageConversion.ConvertWriteAsync),
/// never as blocks. The stored rows are read by SQL, so repair on open cannot hide a block a write kept.</summary>
public sealed class RequirementWriteConversionTests
{
    private const string B1 = "00000000-0000-4000-8000-0000000000b1", B2 = "00000000-0000-4000-8000-0000000000b2";

    private static string Old() => Json(Heading("Portál", "portal", 2),
        Block(B1, "portal.ssl", "SSL", state: "waiting_for_input", direction: "inbound", date: "2026-11-15", party: "IT KBN"),
        Block(B2, "portal.dns", "DNS", requires: "portal.ssl"));

    private static Task<string> StoredAsync(ProjectorFixture f, Guid page) => TextAsync(f, """SELECT "ContentJson" FROM "PageRevision" WHERE "PageId"=:id AND "ActiveTo" IS NULL""", page);

    private static async Task<string> TextAsync(ProjectorFixture f, string sql, Guid page)
    {
        using var session = f.Host.OpenSession();
        return Convert.ToString(await session.CreateSQLQuery(sql).SetParameter("id", page).UniqueResultAsync())!;
    }

    private static async Task<int> VersionAsync(ProjectorFixture f, Guid page)
        => Convert.ToInt32(await f.ScalarAsync("""SELECT "Version" FROM "Page" WHERE "Id"=:id""", page));

    private static void AssertHeadings(string json, Guid page)
    {
        Assert.DoesNotContain("\"type\":\"requirement\"", json, StringComparison.Ordinal);
        var root = JsonNode.Parse(json)!;
        Assert.Contains(root["plugins"]!.AsArray(), x => (string?)x!["id"] == "atlas-requirements" && (int)x["version"]! == 4);
        var content = root["document"]!["content"]!.AsArray();
        var ssl = content.Single(x => (string?)x!["attrs"]?["headingId"] == "portal.ssl")!;
        Assert.Equal(("waiting_for_input", "inbound", "IT KBN"), ((string?)ssl["attrs"]!["state"], (string?)ssl["attrs"]!["direction"], (string?)ssl["attrs"]!["party"]));
        Assert.Equal("do 15. 11. 2026", (string?)content[content.IndexOf(ssl) + 1]!["content"]![0]!["text"]);
        var dns = content.Single(x => (string?)x!["attrs"]?["headingId"] == "portal.dns")!;
        var dependency = content[content.IndexOf(dns) + 1]!;
        Assert.True((bool)dependency["attrs"]!["dependencies"]!);
        Assert.Equal($"#/pages/{page:D}?heading=portal.ssl", (string?)dependency["content"]![0]!["attrs"]!["url"]);
    }

    [PostgreSqlFact]
    public async Task Create_save_source_and_restore_store_requirement_headings_never_blocks()
    {
        await using var f = await ProjectorFixture.CreateAsync();

        var created = (await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "KBN", Old(), null), f.Admin, default)).Id;
        AssertHeadings(await StoredAsync(f, created), created);

        var page = await f.PageAsync("Druhá", Json(Paragraph("Text")), publish: false);
        await f.Pages.SaveAsync(page, new SavePageRequest("Druhá", Old(), null, await VersionAsync(f, page)), f.Admin, default);
        AssertHeadings(await StoredAsync(f, page), page);

        await f.As<PageDto>(f.Admin, new ReplacePageSource(page, new ReplacePageSourceRequest(Old(), null, await VersionAsync(f, page))));
        AssertHeadings(await StoredAsync(f, page), page);

        // A revision stored before 3.6 (blocks, written past the save) restored from history.
        await f.WriteContentAsync(page, Old());
        var history = Guid.Parse(await TextAsync(f, """SELECT "Id" FROM "PageRevision" WHERE "PageId"=:id AND "ActiveTo" IS NULL""", page));
        await f.Pages.SaveAsync(page, new SavePageRequest("Druhá", Json(Paragraph("Jinak")), null, await VersionAsync(f, page)), f.Admin, default);
        await f.As<PageDto>(f.Admin, new RestorePage(page, new RestorePageRequest(history, await VersionAsync(f, page))));
        AssertHeadings(await StoredAsync(f, page), page);
    }

    /// <summary>Old blocks the conversion cannot carry (a block in a quote: the gate sees it lost) are refused, never stored: 400 with the
    /// code requirement_shape over the API, the same refusal through the MCP draft's save, and its own message for a restore of history.</summary>
    [PostgreSqlFact]
    public async Task A_write_the_conversion_cannot_carry_is_refused_with_requirement_shape()
    {
        var quoted = Json(new JsonObject { ["type"] = "blockquote", ["content"] = new JsonArray(Block(B1, "portal.ssl", "SSL")) });
        await using (var site = await AtlasHttpSite.CreateAsync("atlas_requirement_shape_", settings => settings["Modules:Projector:Enabled"] = "true", shared: true))
        {
            using var admin = site.Client();
            await site.CompleteSetupAsync(admin);
            var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
            var refused = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
                new { spaceId, parentId = (string?)null, title = "KBN", contentJson = quoted, markdown = "" }), HttpStatusCode.BadRequest);
            Assert.Equal(("requirement_shape", 400), (refused["code"]!.GetValue<string>(), refused["status"]!.GetValue<int>()));
            Assert.Contains("nadpisy požadavků", refused["title"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("KBN", Json(Paragraph("Text")), publish: false);
        var before = await VersionAsync(f, page);
        var mcp = await Assert.ThrowsAsync<Atlas.Server.Platform.Validation.ApiException>(() => f.As<PageDto>(f.Admin,
            new SaveMcpDraft(page, new SavePageRequest("KBN", quoted, null, before))));
        Assert.Equal((400, "requirement_shape"), (mcp.Status, mcp.Code));
        Assert.Equal(Json(Paragraph("Text")), await StoredAsync(f, page));

        await f.WriteContentAsync(page, quoted);
        var history = Guid.Parse(await TextAsync(f, """SELECT "Id" FROM "PageRevision" WHERE "PageId"=:id AND "ActiveTo" IS NULL""", page));
        await f.Pages.SaveAsync(page, new SavePageRequest("KBN", Json(Paragraph("Jinak")), null, await VersionAsync(f, page)), f.Admin, default);
        var version = await VersionAsync(f, page);
        var restore = await Assert.ThrowsAsync<Atlas.Server.Platform.Validation.ApiException>(() => f.As<PageDto>(f.Admin,
            new RestorePage(page, new RestorePageRequest(history, version))));
        Assert.Equal((400, "requirement_shape"), (restore.Status, restore.Code));
        Assert.StartsWith("Tuto verzi z historie nejde obnovit", restore.Message, StringComparison.Ordinal);
    }
}
