using System.Net;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Mcp;

/// <summary>
/// atlas_get_space_tree, atlas_find_mentions and atlas_explain_access through the C# SDK client: the web's rights and G5 (closed and
/// excluded answer the same 404), the batch shape of one call, the hard caps with truncated + nextCursor, and access details only for
/// whom the web shows them.
/// </summary>
public sealed class McpOverviewToolTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Reader_sees_tree_mentions_and_own_access_only_where_the_web_shows_them()
    {
        await using var f = await McpSite.CreateAsync();
        var (readerClient, readerId) = await f.UserAsync("reader");
        var open = await f.SpaceAsync("Provoz");
        var closed = await f.SpaceAsync("Vedení");
        var excluded = await f.SpaceAsync("Smlouvy");
        await f.GrantAsync(open, readerId, "Reader");
        await f.GrantAsync(excluded, readerId, "Reader");
        var root = await PageAsync(f.Admin, open, "Příručka", Rich(Paragraph("Úvod: lemurit.")), "Úvod: lemurit.");
        var child = await PageAsync(f.Admin, open, "VPN", Rich(Paragraph("Klient lemurit pro všechny.")), "Klient lemurit pro všechny.", parentId: root.Id);
        var draft = await PageAsync(f.Admin, open, "Koncept", Rich(Paragraph("Rozepsaný lemurit.")), "Rozepsaný lemurit.", publish: false);
        var hidden = await PageAsync(f.Admin, closed, "Tajné", Rich(Paragraph("Tajný lemurit.")), "Tajný lemurit.");
        var outside = await PageAsync(f.Admin, excluded, "Smlouva", Rich(Paragraph("Vyloučený lemurit.")), "Vyloučený lemurit.");
        await f.ExcludeFromAiAsync(excluded);
        var labeller = await f.ClientAsync(await f.AdminTokenAsync("read", "write"));
        Assert.NotEqual(true, (await CallAsync(labeller, "atlas_set_decoration", new { pageId = child.Id, icon = "book-open", color = "blue-3" })).IsError);
        Assert.NotEqual(true, (await CallAsync(labeller, "atlas_set_tags", new { pageId = child.Id, add = new[] { "síť", "návod" } })).IsError);
        var reader = await f.ClientAsync((await IssueAsync(readerClient, f.Site.Password, ["read"], ["mcp"])).Secret);

        // The whole tree in one call, depth-first, with labels; the admin's private draft stays out.
        var tree = Structured(await CallAsync(reader, "atlas_get_space_tree", new { spaceId = open }));
        Assert.False(tree["truncated"]!.GetValue<bool>());
        var items = tree["items"]!.AsArray();
        Assert.Equal([root.Id, child.Id], items.Select(x => x!["pageId"]!.GetValue<string>()));
        var vpn = items[1]!;
        Assert.Equal(root.Id, vpn["parentId"]!.GetValue<string>());
        Assert.Equal(1, vpn["depth"]!.GetValue<int>());
        Assert.Equal("Příručka › VPN", vpn["path"]!.GetValue<string>());
        Assert.Equal("book-open", vpn["icon"]!.GetValue<string>());
        Assert.Equal("blue-3", vpn["color"]!.GetValue<string>());
        Assert.Equal(["návod", "síť"], vpn["tags"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.True(vpn["isPublished"]!.GetValue<bool>());
        var adminReader = await f.ClientAsync(await f.AdminTokenAsync("read"));
        var adminTree = Structured(await CallAsync(adminReader, "atlas_get_space_tree", new { spaceId = open }))["items"]!.AsArray();
        Assert.False(adminTree.Single(x => x!["pageId"]!.GetValue<string>() == draft.Id)!["isPublished"]!.GetValue<bool>());

        // Mentions: published text only, readable pages only, never the excluded space.
        var mentions = Structured(await CallAsync(reader, "atlas_find_mentions", new { term = "lemurit" }));
        Assert.Equal(new[] { root.Id, child.Id }.Order(StringComparer.Ordinal),
            mentions["items"]!.AsArray().Select(x => x!["pageId"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        var mention = mentions["items"]!.AsArray().Single(x => x!["pageId"]!.GetValue<string>() == child.Id)!;
        Assert.Equal(f.SpaceName(open) + " › Příručka › VPN", mention["path"]!.GetValue<string>());
        Assert.Contains("lemurit", mention["snippet"]!.GetValue<string>());
        Assert.Empty(Items(await CallAsync(reader, "atlas_find_mentions", new { term = "lemurit", spaceId = excluded })));

        // Closed and excluded answer the same 404 as a missing id, in every tool.
        foreach (var (tool, arguments) in new (string, object)[]
        {
            ("atlas_get_space_tree", new { spaceId = closed }), ("atlas_get_space_tree", new { spaceId = excluded }),
            ("atlas_explain_access", new { pageId = hidden.Id }), ("atlas_explain_access", new { pageId = outside.Id }),
            ("atlas_explain_access", new { spaceId = closed }), ("atlas_explain_access", new { spaceId = excluded })
        })
        {
            var refused = await CallAsync(reader, tool, arguments);
            Assert.Equal("not_found", ErrorCode(refused));
            Assert.DoesNotContain("Smlouva", Text(refused));
        }
        Assert.Equal("not_found", ErrorCode(await CallAsync(adminReader, "atlas_explain_access", new { spaceId = excluded })));

        // A plain reader gets only their own access, as on the web; the list, "view as" and the space grants are refused like there.
        var own = Structured(await CallAsync(reader, "atlas_explain_access", new { pageId = root.Id }));
        Assert.False(own["canManage"]!.GetValue<bool>());
        Assert.Equal(readerId, own["myAccess"]!["userId"]!.GetValue<string>());
        Assert.Null(own["items"]);
        Assert.Null(own["groups"]);
        foreach (var arguments in new object[] { new { pageId = root.Id, userId = f.AdminId }, new { spaceId = open } })
        {
            var refused = await CallAsync(reader, "atlas_explain_access", arguments);
            Assert.Equal(403, Structured(refused)["status"]!.GetValue<int>());
            Assert.DoesNotContain("admin", Text(refused), StringComparison.OrdinalIgnoreCase);
        }

        // The manager sees who and why.
        var people = Items(await CallAsync(adminReader, "atlas_explain_access", new { pageId = root.Id }));
        var readerRow = people.Single(x => x!["userId"]!.GetValue<string>() == readerId)!;
        Assert.Contains("space", readerRow["sources"]!.AsArray().Select(x => x!["kind"]!.GetValue<string>()));
        var explained = await CallAsync(adminReader, "atlas_explain_access", new { pageId = root.Id, userId = readerId });
        Assert.True(Structured(explained)["canView"]!.GetValue<bool>(), Text(explained));
        Assert.False(Structured(explained)["canEdit"]!.GetValue<bool>());
        var grants = Items(await CallAsync(adminReader, "atlas_explain_access", new { spaceId = open }));
        Assert.Contains(grants, x => x!["userId"]?.GetValue<string>() == readerId);
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(adminReader, "atlas_explain_access", new { pageId = root.Id, spaceId = open })));
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(adminReader, "atlas_explain_access", new { spaceId = open, userId = readerId })));
        // Explaining is no page opening.
        Assert.Equal(0L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageAccessEvent\" WHERE \"PageId\"=@page AND \"Channel\"='mcp'", ("page", Guid.Parse(root.Id))));
    }

    [PostgreSqlFact]
    public async Task Tree_and_mentions_stop_at_their_caps_and_continue_with_the_cursor()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        // 501 pages for the tree cap (500); 201 of them published with the term for the mention cap (200).
        var created = new List<string>();
        for (var n = 0; n < 501; n++)
        {
            var mentions = n < 201;
            created.Add((await PageAsync(f.Admin, space, $"Kapitola {n:000}", Rich(Paragraph(mentions ? "Text o kapybarit." : "Text.")),
                mentions ? "Text o kapybarit." : "Text.", publish: mentions)).Id);
        }
        var client = await f.ClientAsync(await f.AdminTokenAsync("read"));

        var first = await CallAsync(client, "atlas_get_space_tree", new { spaceId = space });
        Assert.Equal(500, Items(first).Count);
        Assert.True(Structured(first)["truncated"]!.GetValue<bool>());
        Assert.Equal(501, Structured(first)["total"]!.GetValue<int>());
        var rest = Structured(await CallAsync(client, "atlas_get_space_tree", new { spaceId = space, cursor = Structured(first)["nextCursor"]!.GetValue<string>() }));
        Assert.False(rest["truncated"]!.GetValue<bool>());
        Assert.Equal(created.Order(StringComparer.Ordinal),
            Items(first).Concat(rest["items"]!.AsArray()).Select(x => x!["pageId"]!.GetValue<string>()).Order(StringComparer.Ordinal));

        var found = await CallAsync(client, "atlas_find_mentions", new { term = "kapybarit", spaceId = space });
        Assert.Equal(200, Items(found).Count);
        Assert.True(Structured(found)["truncated"]!.GetValue<bool>());
        var more = await CallAsync(client, "atlas_find_mentions", new { term = "kapybarit", spaceId = space, cursor = Structured(found)["nextCursor"]!.GetValue<string>() });
        Assert.False(Structured(more)["truncated"]!.GetValue<bool>());
        Assert.Equal(created.Take(201).Order(StringComparer.Ordinal),
            Items(found).Concat(Items(more)).Select(x => x!["pageId"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.True(Text(found).Length <= 40_000);
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "atlas_find_mentions",
            new { term = "kapybarit", spaceId = space, cursor = Structured(first)["nextCursor"]!.GetValue<string>() })));
    }

    [PostgreSqlFact]
    public async Task Mentions_find_czech_word_forms_and_keep_short_words_and_phrases_exact()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var accusative = await PageAsync(f.Admin, space, "Nástup", Rich(Paragraph("Zaměstnanec dostane tiskárnu.")), "Zaměstnanec dostane tiskárnu.");
        var locative = await PageAsync(f.Admin, space, "Provoz", Rich(Paragraph("Chyba je v tiskárně a tiskárny jsou dvě.")), "Chyba je v tiskárně a tiskárny jsou dvě.");
        var other = await PageAsync(f.Admin, space, "Kuchyň", Rich(Paragraph("Kolo a kolotoč.")), "Kolo a kolotoč.");
        var client = await f.ClientAsync(await f.AdminTokenAsync("read"));

        var printer = Structured(await CallAsync(client, "atlas_find_mentions", new { term = "tiskárna", spaceId = space }))["items"]!.AsArray();
        Assert.Equal(new[] { accusative.Id, locative.Id }.Order(StringComparer.Ordinal),
            printer.Select(x => x!["pageId"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        // A word of four letters or fewer is not stemmed: "kola" does not become "kol" and match "kolo".
        var short4 = Structured(await CallAsync(client, "atlas_find_mentions", new { term = "kola", spaceId = space }));
        Assert.Empty(short4["items"]!.AsArray());
        // A quoted phrase stays exact.
        var phrase = Structured(await CallAsync(client, "atlas_find_mentions", new { term = "\"tiskárna\"", spaceId = space }));
        Assert.Empty(phrase["items"]!.AsArray());
        Assert.Equal(other.Id, Structured(await CallAsync(client, "atlas_find_mentions", new { term = "kolo", spaceId = space }))["items"]![0]!["pageId"]!.GetValue<string>());
    }
}
