using System.Net;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Mcp;

/// <summary>
/// The icon and tag tools through the C# SDK client: the shared decoration and tag handlers behind G5 with the draft scope, no
/// document revision, and tag suggestions and listings that leave out what lives only in an excluded space.
/// </summary>
public sealed class McpLabelToolTests
{
    [PostgreSqlFact]
    public async Task Icon_and_tags_are_set_and_read_without_a_document_revision()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = await PageAsync(f.Admin, space, "Příručka", Rich(Paragraph("Text.")), "Text.");
        await f.AdminTokenAsync("read");
        var (_, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"]);
        var client = await f.ClientAsync(secret);

        var catalog = Structured(await CallAsync(client, "atlas_list_appearance", new { query = "book-open" }));
        Assert.Contains("book-open", catalog["icons"]!.AsArray().Select(x => x!.GetValue<string>()));
        var set = await CallAsync(client, "atlas_set_decoration", new { pageId = page.Id, icon = "book-open", color = "blue-3" });
        Assert.True(set.IsError != true, Text(set));
        Assert.Contains("nevytváří novou revizi", Text(set));
        var revision = Structured(set)["decoration"]!["revisionId"]!.GetValue<string>();
        Assert.Equal("decoration_changed", ErrorCode(await CallAsync(client, "atlas_set_decoration", new { pageId = page.Id, icon = "star", color = "red-2" })));
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "atlas_set_decoration", new { pageId = page.Id, icon = "star", expectedRevisionId = revision })));

        // A missing required argument (tagId) or a wrong name (pageId for objectId) is the caller's input: 400, not a failed tool.
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "atlas_find_pages_by_tag", new { })));
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "atlas_get_relations", new { pageId = page.Id })));

        var tagged = await CallAsync(client, "atlas_set_tags", new { pageId = page.Id, add = new[] { "příručka", "DEMO" } });
        Assert.True(tagged.IsError != true, Text(tagged));
        var changed = await CallAsync(client, "atlas_set_tags", new { pageId = page.Id, add = new[] { "k revizi" }, remove = new[] { "demo" } });
        Assert.Equal(["k revizi", "příručka"], Structured(changed)["tags"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()));
        // A full set needs the current ids; a stale list names them.
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "atlas_set_tags", new { pageId = page.Id, tags = new[] { "jen" } })));
        var stale = await CallAsync(client, "atlas_set_tags", new { pageId = page.Id, tags = new[] { "jen" }, expectedTagIds = Array.Empty<string>() });
        Assert.Equal("tags_changed", ErrorCode(stale));
        Assert.Equal(2, Structured(stale)["currentTagIds"]!.AsArray().Count);

        var labels = Structured(await CallAsync(client, "atlas_get_labels", new { pageId = page.Id }));
        Assert.Equal("book-open", labels["decoration"]!["icon"]!.GetValue<string>());
        Assert.Equal("blue-3", labels["decoration"]!["color"]!.GetValue<string>());
        var tagId = labels["tags"]![0]!["id"]!.GetValue<string>();
        var found = Items(await CallAsync(client, "atlas_find_pages_by_tag", new { tagId, spaceId = space }));
        Assert.Equal(page.Id, Assert.Single(found)!["pageId"]!.GetValue<string>());

        var cleared = await CallAsync(client, "atlas_set_decoration", new { pageId = page.Id, clear = true, expectedRevisionId = revision });
        Assert.True(cleared.IsError != true, Text(cleared));
        // Neither the icon nor the tags are a document change.
        var id = Guid.Parse(page.Id);
        Assert.Equal(1L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageRevision\" WHERE \"PageId\"=@page", ("page", id)));
        Assert.Equal(page.Version, (int)(await f.Site.ScalarAsync("SELECT \"Version\" FROM \"Page\" WHERE \"Id\"=@page", ("page", id)))!);
    }

    [PostgreSqlFact]
    public async Task Excluded_space_hides_pages_and_tags_and_a_read_token_cannot_write()
    {
        await using var f = await McpSite.CreateAsync();
        var open = await f.FirstSpaceAsync();
        var excluded = await f.SpaceAsync("Smlouvy");
        var visible = await PageAsync(f.Admin, open, "Veřejná", Rich(Paragraph("Text.")), "Text.");
        var secret = await PageAsync(f.Admin, excluded, "Smlouva", Rich(Paragraph("Text.")), "Text.");
        // Tag names of this test's own: the tag catalog is the whole site's.
        var word = string.Concat(Guid.NewGuid().ToString("N")[..10].Select(c => (char)('a' + c % 16)));
        foreach (var (page, name) in new[] { (visible.Id, "verejny-" + word), (secret.Id, "smluvni-" + word) })
            await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/pages/{page}/tags", new { names = new[] { name }, expectedTagIds = Array.Empty<string>() }), HttpStatusCode.OK);
        var hiddenTag = (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/pages/{secret.Id}/tags"), HttpStatusCode.OK))["tags"]![0]!["id"]!.GetValue<string>();
        await f.ExcludeFromAiAsync(excluded);
        await f.AdminTokenAsync("read");
        var (_, writeSecret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"]);
        var client = await f.ClientAsync(writeSecret);

        // The excluded page answers as a missing one, for a read and for both writes; so do its tag's listing and suggestion.
        var missing = Text(await CallAsync(client, "atlas_get_labels", new { pageId = Guid.NewGuid().ToString() }));
        Assert.Equal("not_found", ErrorCode(await CallAsync(client, "atlas_get_labels", new { pageId = secret.Id })));
        Assert.Equal(missing, Text(await CallAsync(client, "atlas_get_labels", new { pageId = secret.Id })));
        Assert.Equal("not_found", ErrorCode(await CallAsync(client, "atlas_set_decoration", new { pageId = secret.Id, icon = "star", color = "red-2" })));
        Assert.Equal("not_found", ErrorCode(await CallAsync(client, "atlas_set_tags", new { pageId = secret.Id, add = new[] { "x" } })));
        Assert.Empty(Items(await CallAsync(client, "atlas_find_pages_by_tag", new { tagId = hiddenTag })));
        Assert.Empty(Items(await CallAsync(client, "atlas_suggest_tags", new { query = "smluvni-" + word })));
        var names = Items(await CallAsync(client, "atlas_suggest_tags", new { query = word })).Select(x => x!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(["verejny-" + word], names);
        Assert.Equal(0L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageDecoration\" WHERE \"PageId\"=@page", ("page", Guid.Parse(secret.Id))));

    }

    [PostgreSqlFact]
    public async Task Apply_labels_saves_every_change_at_once_without_a_document_revision()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var pages = new List<(string Id, int Version)>();
        foreach (var title in new[] { "Síť", "Tiskárny", "VPN" })
            pages.Add(await PageAsync(f.Admin, space, title, Rich(Paragraph("Text.")), "Text."));
        var readSecret = await f.AdminTokenAsync("read");
        var (tokenId, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"]);
        var client = await f.ClientAsync(secret);
        var revision = Structured(await CallAsync(client, "atlas_set_decoration", new { pageId = pages[2].Id, icon = "star", color = "red-2" }))["decoration"]!["revisionId"]!.GetValue<string>();

        var applied = await CallAsync(client, "atlas_apply_labels", new
        {
            changes = new object[]
            {
                new { pageId = pages[0].Id, icon = "network", color = "blue-3", addTags = new[] { "IT", "síť" } },
                new { pageId = pages[1].Id, icon = "server", color = "slate-3", addTags = new[] { "it" } },
                new { pageId = pages[2].Id, clearDecoration = true, expectedRevisionId = revision, addTags = new[] { "návod" } }
            }
        });
        var items = Items(applied);
        Assert.Contains("nevytváří novou revizi", Text(applied));
        Assert.Equal([0, 1, 2], items.Select(x => x!["index"]!.GetValue<int>()));
        Assert.Equal(["IT", "síť"], items[0]!["tags"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        var labels = Structured(await CallAsync(client, "atlas_get_labels", new { pageId = pages[1].Id }));
        Assert.Equal("server", labels["decoration"]!["icon"]!.GetValue<string>());
        // "IT" and "it" are one catalog tag, shared by both pages.
        Assert.Equal(labels["tags"]![0]!["id"]!.GetValue<string>(),
            items[0]!["tags"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "IT")!["id"]!.GetValue<string>());
        Assert.Null(Structured(await CallAsync(client, "atlas_get_labels", new { pageId = pages[2].Id }))["decoration"]!["icon"]);
        foreach (var page in pages)
        {
            var id = Guid.Parse(page.Id);
            Assert.Equal(1L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageRevision\" WHERE \"PageId\"=@page", ("page", id)));
            Assert.Equal(page.Version, (int)(await f.Site.ScalarAsync("SELECT \"Version\" FROM \"Page\" WHERE \"Id\"=@page", ("page", id)))!);
        }
        // Audited per item.
        Assert.Equal(3L, await f.Site.ScalarAsync("SELECT count(DISTINCT \"PageId\") FROM \"ApiActivity\" WHERE \"Operation\"='atlas_apply_labels' AND \"Outcome\"='ok' AND \"TokenId\"=@token",
            ("token", Guid.Parse(tokenId))));

        // A read token cannot write.
        var reader = await f.ClientAsync(readSecret);
        Assert.Equal("scope_missing", ErrorCode(await CallAsync(reader, "atlas_apply_labels", new { changes = new[] { new { pageId = pages[0].Id, addTags = new[] { "x" } } } })));
        // More than 100 changes, a page twice, an item without a change: refused before anything runs, naming the item.
        var tooMany = Enumerable.Range(0, 101).Select(_ => new { pageId = Guid.NewGuid().ToString(), addTags = new[] { "x" } }).ToArray();
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "atlas_apply_labels", new { changes = tooMany })));
        var twice = await CallAsync(client, "atlas_apply_labels",
            new { changes = new[] { new { pageId = pages[0].Id, addTags = new[] { "x" } }, new { pageId = pages[0].Id, addTags = new[] { "y" } } } });
        Assert.Equal("invalid_argument", ErrorCode(twice));
        Assert.Contains("changes[1]", Text(twice));
        Assert.Contains("changes[0]", Text(await CallAsync(client, "atlas_apply_labels", new { changes = new[] { new { pageId = pages[0].Id } } })));
    }

    [PostgreSqlFact]
    public async Task Apply_labels_saves_nothing_when_an_item_fails_and_names_each_failing_item()
    {
        await using var f = await McpSite.CreateAsync();
        var open = await f.FirstSpaceAsync();
        var excluded = await f.SpaceAsync("Smlouvy");
        var first = await PageAsync(f.Admin, open, "Síť", Rich(Paragraph("Text.")), "Text.");
        var tagged = await PageAsync(f.Admin, open, "Tiskárny", Rich(Paragraph("Text.")), "Text.");
        var hidden = await PageAsync(f.Admin, excluded, "Smlouva", Rich(Paragraph("Text.")), "Text.");
        await f.ExcludeFromAiAsync(excluded);
        await f.AdminTokenAsync("read");
        var (tokenId, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"]);
        var client = await f.ClientAsync(secret);
        Assert.NotEqual(true, (await CallAsync(client, "atlas_set_tags", new { pageId = tagged.Id, add = new[] { "tisk" } })).IsError);
        // A tag name of this test's own: the tag catalog is the whole site's.
        var newTag = "novy-stitek-" + string.Concat(Guid.NewGuid().ToString("N")[..10].Select(c => (char)('a' + c % 16)));

        // A stale expectedTagIds on the second item: the first item's icon and new tag are not saved either.
        var stale = await CallAsync(client, "atlas_apply_labels", new
        {
            changes = new object[]
            {
                new { pageId = first.Id, icon = "network", color = "blue-3", addTags = new[] { newTag } },
                new { pageId = tagged.Id, addTags = new[] { "it" }, expectedTagIds = Array.Empty<string>() }
            }
        });
        Assert.Equal("tags_changed", ErrorCode(stale));
        var failed = Assert.Single(Structured(stale)["items"]!.AsArray())!;
        Assert.Equal(1, failed["index"]!.GetValue<int>());
        Assert.Equal(tagged.Id, failed["pageId"]!.GetValue<string>());
        Assert.Single(failed["currentTagIds"]!.AsArray());
        Assert.Contains("changes[1]", Text(stale));
        Assert.Equal(0L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageDecoration\" WHERE \"PageId\"=@page", ("page", Guid.Parse(first.Id))));
        Assert.Equal(0L, await f.Site.ScalarAsync("SELECT count(*) FROM \"Tag\" WHERE \"NormalizedName\"=@name", ("name", newTag)));
        Assert.Equal("rolled_back,tags_changed", await f.Site.ScalarAsync(
            "SELECT string_agg(\"Outcome\", ',' ORDER BY \"Outcome\") FROM \"ApiActivity\" WHERE \"Operation\"='atlas_apply_labels' AND \"TokenId\"=@token",
            ("token", Guid.Parse(tokenId))));

        // An excluded page answers as a missing one, an unknown icon names its item; every failing item is listed and nothing is saved.
        var refused = await CallAsync(client, "atlas_apply_labels", new
        {
            changes = new object[]
            {
                new { pageId = hidden.Id, addTags = new[] { "x" } },
                new { pageId = first.Id, icon = "no-such-icon", color = "blue-3" },
                new { pageId = Guid.NewGuid().ToString(), addTags = new[] { "x" } },
                new { pageId = tagged.Id, addTags = new[] { "it" } }
            }
        });
        var errors = Structured(refused)["items"]!.AsArray();
        Assert.Equal([0, 1, 2], errors.Select(x => x!["index"]!.GetValue<int>()));
        Assert.Equal(["not_found", "invalid_argument", "not_found"], errors.Select(x => x!["code"]!.GetValue<string>()));
        Assert.Equal(errors[2]!["message"]!.GetValue<string>(), errors[0]!["message"]!.GetValue<string>());
        Assert.Equal(["tisk"], Structured(await CallAsync(client, "atlas_get_labels", new { pageId = tagged.Id }))["tags"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()));
        Assert.Equal(0L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageTag\" WHERE \"PageId\"=@page", ("page", Guid.Parse(hidden.Id))));
        Assert.Equal(0L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageDecoration\" WHERE \"PageId\"=@page", ("page", Guid.Parse(first.Id))));
    }
}
