using System.Net;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Mcp;

/// <summary>
/// The page organization tools (spec section 4) through the C# SDK client. The rules themselves (cycles, hidden parents, rights, the
/// live-edit guard) belong to the shared handlers and their tests; here the MCP layer: the scope publish, G5 under the locks, the
/// tree version and the page version handed from the tree to the writes, conflicts that change nothing, the explicit scopes, and the
/// round trip through the trash.
/// </summary>
public sealed class McpOrganizationToolTests
{
    private static readonly string[] WriteTools = ["atlas_move_page", "atlas_arrange_pages", "atlas_sort_pages", "atlas_move_page_to_space", "atlas_trash_page", "atlas_restore_page"];

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Pages_move_under_a_hub_and_are_reordered_among_siblings()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var hub = await NewPageAsync(f, space, "Rozcestník");
        var first = await NewPageAsync(f, space, "První příručka");
        var second = await NewPageAsync(f, space, "Druhá příručka");
        var (tokenId, client) = await PublisherAsync(f);

        // The tree hands over what the writes need: tree version, page versions and the position among siblings.
        var tree = Structured(await CallAsync(client, "atlas_list_pages", new { spaceId = space }));
        var treeVersion = tree["treeVersion"]!.GetValue<long>();
        var listed = tree["items"]!.AsArray().Single(x => x!["pageId"]!.GetValue<string>() == first)!;
        Assert.Equal(1, listed["version"]!.GetValue<int>());
        Assert.True(listed["canEdit"]!.GetValue<bool>());
        Assert.Equal(2, listed["position"]!.GetValue<int>());

        var moved = await CallAsync(client, "atlas_move_page", new { pageId = first, expectedTreeVersion = treeVersion, parentId = hub });
        Assert.True(moved.IsError != true, Text(moved));
        var afterFirst = Structured(moved)["treeVersion"]!.GetValue<long>();
        Assert.True(afterFirst > treeVersion);
        Assert.Equal(afterFirst, Structured(moved)["expectedTreeVersion"]!.GetValue<long>());
        Assert.Contains("okamžitě", Text(moved));
        var secondMoved = await CallAsync(client, "atlas_move_page", new { pageId = second, expectedTreeVersion = afterFirst, parentId = hub });
        var afterSecond = Structured(secondMoved)["treeVersion"]!.GetValue<long>();
        Assert.Equal(await ParentAsync(f, second), Guid.Parse(hub));

        // The same parent and another position: the second page goes before the first.
        var reordered = await CallAsync(client, "atlas_move_page", new { pageId = second, expectedTreeVersion = afterSecond, parentId = hub, beforePageId = first });
        Assert.True(reordered.IsError != true, Text(reordered));
        var level = Structured(await CallAsync(client, "atlas_list_pages", new { spaceId = space, parentId = hub }));
        Assert.Equal([second, first], level["items"]!.AsArray().Select(x => x!["pageId"]!.GetValue<string>()));
        Assert.Equal([1, 2], level["items"]!.AsArray().Select(x => x!["position"]!.GetValue<int>()));

        // And back to the root, with every attempt audited under its tool.
        var root = await CallAsync(client, "atlas_move_page", new { pageId = first, expectedTreeVersion = Structured(reordered)["treeVersion"]!.GetValue<long>() });
        Assert.True(root.IsError != true, Text(root));
        Assert.Equal(DBNull.Value, await f.Site.ScalarAsync("SELECT \"ParentId\" FROM \"Page\" WHERE \"Id\"=@page", ("page", Guid.Parse(first))) ?? DBNull.Value);
        Assert.Equal(Enumerable.Repeat("atlas_move_page:ok", 4), await AuditAsync(f, tokenId));
    }

    [PostgreSqlFact]
    public async Task A_menu_is_arranged_in_one_call_and_one_refused_move_undoes_the_others()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var hub = await NewPageAsync(f, space, "Rozcestník");
        var first = await NewPageAsync(f, space, "První příručka");
        var second = await NewPageAsync(f, space, "Druhá příručka");
        var (_, client) = await PublisherAsync(f);
        var tree = Structured(await CallAsync(client, "atlas_list_pages", new { spaceId = space }))["treeVersion"]!.GetValue<long>();

        // Each move sees the earlier ones: the second page goes under the hub before the first, which got there a move earlier.
        var arranged = await CallAsync(client, "atlas_arrange_pages", new
        {
            expectedTreeVersion = tree,
            moves = new object[] { new { pageId = first, parentId = hub }, new { pageId = second, parentId = hub, beforePageId = first } },
        });
        Assert.True(arranged.IsError != true, Text(arranged));
        var after = Structured(arranged)["treeVersion"]!.GetValue<long>();
        Assert.True(after > tree);
        var level = Structured(await CallAsync(client, "atlas_list_pages", new { spaceId = space, parentId = hub }));
        Assert.Equal([second, first], level["items"]!.AsArray().Select(x => x!["pageId"]!.GetValue<string>()));

        // The first move is fine, the second names a sibling that is no longer under that parent: nothing moves and the error says which.
        var refused = await CallAsync(client, "atlas_arrange_pages", new
        {
            expectedTreeVersion = after,
            moves = new object[] { new { pageId = first }, new { pageId = second, parentId = hub, beforePageId = first } },
        });
        Assert.True(refused.IsError);
        var failing = Structured(refused)["items"]!.AsArray().Single()!;
        Assert.Equal((1, second), (failing["index"]!.GetValue<int>(), failing["pageId"]!.GetValue<string>()));
        Assert.Equal(Guid.Parse(hub), await ParentAsync(f, first));
        Assert.Equal(after, Structured(await CallAsync(client, "atlas_list_pages", new { spaceId = space }))["treeVersion"]!.GetValue<long>());

        // A page twice is refused before anything runs.
        var twice = await CallAsync(client, "atlas_arrange_pages", new { expectedTreeVersion = after, moves = new object[] { new { pageId = first }, new { pageId = first, parentId = hub } } });
        Assert.Equal("invalid_argument", ErrorCode(twice));
    }

    /// <summary>„Řadit abecedně“ for AI (3.6): the tree shows the pin, a hand move within the level ends it and says how to pin it again,
    /// and the rights are the web's.</summary>
    [PostgreSqlFact]
    public async Task A_level_sorted_by_title_ends_with_a_hand_move_within_it()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.SpaceAsync("Řazení");
        var zeta = await NewPageAsync(f, space, "Zeta");
        var alfa = await NewPageAsync(f, space, "Alfa");
        var (_, client) = await PublisherAsync(f);

        var sorted = await CallAsync(client, "atlas_sort_pages", new { spaceId = space, byTitle = true });
        Assert.True(sorted.IsError != true, Text(sorted));
        var tree = Structured(await CallAsync(client, "atlas_get_space_tree", new { spaceId = space }));
        Assert.True(tree["rootByTitle"]!.GetValue<bool>());
        Assert.Equal([alfa, zeta], tree["items"]!.AsArray().Select(x => x!["pageId"]!.GetValue<string>()));

        var moved = await CallAsync(client, "atlas_move_page", new { pageId = zeta, expectedTreeVersion = tree["treeVersion"]!.GetValue<long>(), beforePageId = alfa });
        Assert.True(moved.IsError != true, Text(moved));
        Assert.True(Structured(moved)["titleOrderCancelled"]!.GetValue<bool>());
        Assert.Contains("atlas_sort_pages", Text(moved));

        // A reader of the space may not sort it: the 403 of the space root, as for a root page, and nothing changes.
        var (reader, readerId) = await f.UserAsync("ctenar", "write");
        await f.GrantAsync(space, readerId, "Reader");
        var (_, secret) = await IssueAsync(reader, f.Site.Password, ["read", "write"], ["mcp"]);
        var refused = await CallAsync(await f.ClientAsync(secret), "atlas_sort_pages", new { spaceId = space, byTitle = true });
        Assert.Equal("forbidden", ErrorCode(refused));
        Assert.Equal(false, await f.Site.ScalarAsync("""SELECT "SortRootByTitle" FROM "SpaceOrganization" WHERE "SpaceId"=@space""", ("space", Guid.Parse(space))));
    }

    [PostgreSqlFact]
    public async Task A_stale_tree_version_changes_nothing()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var hub = await NewPageAsync(f, space, "Rozcestník");
        var page = await NewPageAsync(f, space, "Příručka");
        var (_, client) = await PublisherAsync(f);
        var stale = Structured(await CallAsync(client, "atlas_list_pages", new { spaceId = space }))["treeVersion"]!.GetValue<long>();
        // Somebody else changes the tree in between (the web).
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/pages/{hub}/move",
            new { parentId = (string?)null, beforePageId = (string?)null, expectedTreeVersion = stale })).StatusCode);

        var refused = await CallAsync(client, "atlas_move_page", new { pageId = page, expectedTreeVersion = stale, parentId = hub });
        Assert.Equal("tree_conflict", ErrorCode(refused));
        Assert.Equal(409, Structured(refused)["status"]!.GetValue<int>());
        Assert.True(Structured(refused)["currentTreeVersion"]!.GetValue<long>() > stale);
        Assert.Contains("atlas_list_pages", Text(refused));
        Assert.Equal(DBNull.Value, await f.Site.ScalarAsync("SELECT \"ParentId\" FROM \"Page\" WHERE \"Id\"=@page", ("page", Guid.Parse(page))) ?? DBNull.Value);

        // A stale page version stops the trash the same way.
        var current = Structured(await CallAsync(client, "atlas_list_pages", new { spaceId = space }))["treeVersion"]!.GetValue<long>();
        var conflict = await CallAsync(client, "atlas_trash_page", new { pageId = page, expectedVersion = 7, expectedTreeVersion = current, includeSubtree = false });
        Assert.Equal("version_conflict", ErrorCode(conflict));
        Assert.Equal(1, Structured(conflict)["currentVersion"]!.GetValue<int>());
        Assert.Equal(false, await f.Site.ScalarAsync("SELECT \"IsTrashed\" FROM \"Page\" WHERE \"Id\"=@page", ("page", Guid.Parse(page))));
    }

    [PostgreSqlFact]
    public async Task Trash_needs_an_explicit_subtree_scope_and_restore_brings_everything_back()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var hub = await NewPageAsync(f, space, "Rozcestník");
        var child = await NewPageAsync(f, space, "Příručka", hub);
        await UploadAsync(f, child);
        var content = await f.Site.ScalarAsync("SELECT \"ContentJson\" FROM \"PageRevision\" WHERE \"PageId\"=@page AND \"ActiveTo\" IS NULL", ("page", Guid.Parse(child)));
        var (_, client) = await PublisherAsync(f);
        var tree = Structured(await CallAsync(client, "atlas_list_pages", new { spaceId = space }));
        var version = tree["items"]!.AsArray().Single(x => x!["pageId"]!.GetValue<string>() == hub)!["version"]!.GetValue<int>();

        var unscoped = await CallAsync(client, "atlas_trash_page", new
        {
            pageId = hub, expectedVersion = version, expectedTreeVersion = tree["treeVersion"]!.GetValue<long>(), includeSubtree = false
        });
        Assert.Equal("subtree_confirmation_required", ErrorCode(unscoped));
        Assert.Equal(0L, await f.Site.ScalarAsync("SELECT count(*) FROM \"Page\" WHERE \"IsTrashed\" AND \"SpaceId\"=@space", ("space", Guid.Parse(space))));

        var trashed = await CallAsync(client, "atlas_trash_page", new
        {
            pageId = hub, expectedVersion = version, expectedTreeVersion = tree["treeVersion"]!.GetValue<long>(), includeSubtree = true
        });
        Assert.True(trashed.IsError != true, Text(trashed));
        Assert.Equal(2, Structured(trashed)["affectedIds"]!.AsArray().Count);
        Assert.Empty(Items(await CallAsync(client, "atlas_list_pages", new { spaceId = space })));

        // The trash lists both pages of the batch and hands over the tree version the restore needs.
        var trash = await CallAsync(client, "atlas_list_trash", new { spaceId = space });
        var listed = Items(trash);
        Assert.Equal(new[] { hub, child }.Order(), listed.Select(x => x!["pageId"]!.GetValue<string>()).Order());
        Assert.Single(listed.Select(x => x!["trashBatchId"]!.GetValue<string>()).Distinct());
        var restored = await CallAsync(client, "atlas_restore_page", new
        {
            pageId = hub, expectedTreeVersion = Structured(trash)["treeVersion"]!.GetValue<long>(), includeSubtree = true
        });
        Assert.True(restored.IsError != true, Text(restored));
        Assert.Empty(Items(await CallAsync(client, "atlas_list_trash", new { spaceId = space })));
        Assert.Equal([hub], Items(await CallAsync(client, "atlas_list_pages", new { spaceId = space })).Select(x => x!["pageId"]!.GetValue<string>()));

        // Content, publication and the attachment are what they were.
        Assert.Equal(content, await f.Site.ScalarAsync("SELECT \"ContentJson\" FROM \"PageRevision\" WHERE \"PageId\"=@page AND \"ActiveTo\" IS NULL", ("page", Guid.Parse(child))));
        Assert.Equal(1L, await f.Site.ScalarAsync("SELECT count(*) FROM \"Attachment\" WHERE \"PageId\"=@page AND \"ActiveTo\" IS NULL", ("page", Guid.Parse(child))));
        Assert.Contains("Příručka", Text(await CallAsync(client, "atlas_get_page", new { pageId = child })));
    }

    [PostgreSqlFact]
    public async Task A_move_between_spaces_needs_both_spaces_in_mcp_and_the_acknowledgement()
    {
        await using var f = await McpSite.CreateAsync();
        var source = await f.FirstSpaceAsync();
        var target = await f.SpaceAsync("Provoz");
        var hidden = await f.SpaceAsync("Smlouvy");
        await f.ExcludeFromAiAsync(hidden);
        var page = await NewPageAsync(f, source, "Příručka");
        var (_, client) = await PublisherAsync(f);
        long TreeOf(JsonNode tree) => tree["treeVersion"]!.GetValue<long>();
        var sourceTree = TreeOf(Structured(await CallAsync(client, "atlas_list_pages", new { spaceId = source })));
        var targetTree = TreeOf(Structured(await CallAsync(client, "atlas_list_pages", new { spaceId = target })));

        object Arguments(string to, bool acknowledge) => new
        {
            pageId = page, targetSpaceId = to, expectedSourceTreeVersion = sourceTree, expectedTargetTreeVersion = targetTree,
            expectedVersion = 1, includeSubtree = true, acknowledgeAccessChange = acknowledge
        };
        // An excluded target answers exactly as a space that does not exist, and nothing changes; so does a source that is excluded.
        var excluded = await CallAsync(client, "atlas_move_page_to_space", Arguments(hidden, true));
        Assert.Equal("not_found", ErrorCode(excluded));
        Assert.Equal(Text(await CallAsync(client, "atlas_move_page_to_space", Arguments(Guid.NewGuid().ToString(), true))), Text(excluded));
        Assert.Equal(Guid.Parse(source), await f.Site.ScalarAsync("SELECT \"SpaceId\" FROM \"Page\" WHERE \"Id\"=@page", ("page", Guid.Parse(page))));

        // The acknowledgement is explicit; with it the page lands in the target space and both tree versions move on.
        Assert.Equal("access_change_unacknowledged", ErrorCode(await CallAsync(client, "atlas_move_page_to_space", Arguments(target, false))));
        var moved = await CallAsync(client, "atlas_move_page_to_space", Arguments(target, true));
        Assert.True(moved.IsError != true, Text(moved));
        Assert.True(Structured(moved)["targetTreeVersion"]!.GetValue<long>() > targetTree);
        Assert.Equal(Guid.Parse(target), await f.Site.ScalarAsync("SELECT \"SpaceId\" FROM \"Page\" WHERE \"Id\"=@page", ("page", Guid.Parse(page))));
    }

    [PostgreSqlFact]
    public async Task A_read_token_is_not_offered_the_organization_writes()
    {
        await using var f = await McpSite.CreateAsync();
        await f.AdminTokenAsync("read");
        var (_, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read"], ["mcp"]);
        var client = await f.ClientAsync(secret);

        var tools = (await client.ListToolsAsync()).Select(x => x.Name).ToArray();
        Assert.All(WriteTools, tool => Assert.DoesNotContain(tool, tools));
        Assert.Contains("atlas_list_trash", tools);
    }

    // ---- helpers ----

    private static async Task<string> NewPageAsync(McpSite f, string space, string title, string? parent = null)
        => (await PageAsync(f.Admin, space, title, Rich(Paragraph(title + " obsah.")), title + " obsah.", true, parent)).Id;

    /// <summary>The administrator's token with read and publish on the channel mcp: its id and a connected client.</summary>
    private static async Task<(string TokenId, McpClient Client)> PublisherAsync(McpSite f)
    {
        await f.AdminTokenAsync("read");
        var (id, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"]);
        return (id, await f.ClientAsync(secret));
    }

    private static async Task<Guid> ParentAsync(McpSite f, string pageId)
        => (Guid)(await f.Site.ScalarAsync("SELECT \"ParentId\" FROM \"Page\" WHERE \"Id\"=@page", ("page", Guid.Parse(pageId))))!;

    /// <summary>The audit rows of a token in order: tool:outcome.</summary>
    private static async Task<string[]> AuditAsync(McpSite f, string tokenId)
    {
        var rows = (string?)await f.Site.ScalarAsync("""
            SELECT string_agg("Operation" || ':' || "Outcome", '|' ORDER BY "Id") FROM "ApiActivity" WHERE "TokenId"=@token
            """, ("token", Guid.Parse(tokenId)));
        return rows?.Split('|') ?? [];
    }

    /// <summary>A small file attached to the page by the web, as an author would.</summary>
    private static async Task UploadAsync(McpSite f, string pageId)
    {
        var csrf = await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/auth/csrf"), HttpStatusCode.OK);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("příloha"u8.ToArray()), "file", "priloha.pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/pages/{pageId}/attachments") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Created, (await f.Admin.SendAsync(request)).StatusCode);
    }
}
